using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using MSXML;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Core.Snippets;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Editor;

namespace SqlAssist.Ssms22.Snippets;

/// <summary>游標所在的原生 Snippet 欄位。</summary>
/// <remarks>
/// 帶著預設值一起走，是為了分辨「這一格還是樣板填的字」與「使用者已經打了字」。
/// 兩者的上下文完全不同：前者要當它不存在（<c>MERGE INTO |</c> 才推得出資料來源），
/// 後者那幾個字就是使用者的篩選前綴。少了這個分辨，無限定字的格子
/// （<c>INSERT (|)</c>）因為前綴永遠是空的而永遠不參與，
/// 而剛進格時整格預設值又會被排名器當成前綴、把清單濾光。
/// </remarks>
internal readonly struct SqlSnippetFieldSpan
{
    public SqlSnippetFieldSpan(SnapshotSpan span, string defaultValue)
    {
        Span = span;
        DefaultValue = defaultValue;
        HoldsDefault = string.Equals(span.GetText(), defaultValue, StringComparison.Ordinal);
    }

    public SnapshotSpan Span { get; }

    /// <summary>樣板為這一格填的預設值。</summary>
    public string DefaultValue { get; }

    /// <summary>格子裡還是樣板填的字，使用者一個都還沒打。</summary>
    /// <remarks>
    /// 在建立時就算好：提交那一步拿到的快照與判定當下未必是同一份，
    /// 而要問的是「判定當下」的狀態。使用者剛好打出與預設值一模一樣的內容時，
    /// 結果只是那一次列出完整清單，沒有壞處。
    /// </remarks>
    public bool HoldsDefault { get; }
}

internal enum NativeSnippetInsertionResult
{
    Succeeded,
    FailedWithoutChange,
    FailedAfterChange
}

internal sealed class SqlSnippetExpansionRequest
{
    public SqlSnippetExpansionRequest(
        ITextBuffer buffer,
        ITrackingSpan span,
        string expectedText,
        SqlSnippet snippet)
    {
        Buffer = buffer;
        Span = span;
        ExpectedText = expectedText;
        Snippet = snippet;
    }

    public ITextBuffer Buffer { get; }

    public ITrackingSpan Span { get; }

    public string ExpectedText { get; }

    public SqlSnippet Snippet { get; }
}

/// <summary>每個 SQL 編輯器各自的一個原生 Snippet session。</summary>
internal sealed class SqlSnippetExpansionController : IDisposable
{
    private static readonly object PropertyKey = new();

    private readonly IWpfTextView _textView;
    private readonly IVsEditorAdaptersFactoryService _adapters;

    /// <summary>進入欄位之後要把建議清單重開一次；缺席時只是沒有清單。</summary>
    private readonly IAsyncCompletionBroker? _broker;

    private readonly SqlSnippetExpansionClient _client;
    private IVsExpansionSession? _session;

    /// <summary>目前 session 展開的是哪一筆片段。</summary>
    /// <remarks>
    /// 只為了問引擎欄位範圍而留：<see cref="IVsExpansionSession.GetFieldSpan"/> 要
    /// 欄位名稱，而引擎不提供「目前在哪一格」，也沒有列舉欄位的方法。名稱只能由
    /// 我們自己這一份定義供給。
    /// </remarks>
    private SqlSnippet? _snippet;

    /// <summary>目前 session 所在的緩衝區；<c>FormatSpan</c> 的行號要對到它。</summary>
    /// <remarks>
    /// 必須在 <c>InsertSpecificExpansion</c> 之前就設好——那個回呼是在插入的
    /// 呼叫堆疊裡發生的，等呼叫返回才設就永遠來不及。
    /// </remarks>
    private ITextBuffer? _buffer;

    /// <summary>只有插入那一次要補縮排。</summary>
    /// <remarks>
    /// 引擎在欄位導覽時也可能再叫一次 <c>FormatSpan</c>，而這裡補的是「插入」而不是
    /// 「設定」縮排，每叫一次就多推一層。用一次性的旗標把它夾死，比事後判斷
    /// 「這一行是不是已經縮排過」可靠。
    /// </remarks>
    private bool _formatPending;
    private bool _disposed;

    private SqlSnippetExpansionController(
        IWpfTextView textView,
        IVsEditorAdaptersFactoryService adapters,
        IAsyncCompletionBroker? broker)
    {
        _textView = textView;
        _adapters = adapters;
        _broker = broker;
        _client = new SqlSnippetExpansionClient(this);
        _textView.Closed += OnTextViewClosed;
    }

    public bool HasActiveSession => _session is not null;

    public static void Attach(
        IWpfTextView textView,
        IVsEditorAdaptersFactoryService adapters,
        IAsyncCompletionBroker? broker)
    {
        textView.Properties.GetOrCreateSingletonProperty(
            PropertyKey,
            () => new SqlSnippetExpansionController(textView, adapters, broker));
    }

    public static SqlSnippetExpansionController? Peek(ITextView textView)
    {
        return textView.Properties.TryGetProperty(
            PropertyKey,
            out SqlSnippetExpansionController controller)
            ? controller
            : null;
    }

    public NativeSnippetInsertionResult TryInsert(SqlSnippetExpansionRequest request)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_disposed || _textView.IsClosed)
        {
            return NativeSnippetInsertionResult.FailedWithoutChange;
        }

        var before = request.Buffer.CurrentSnapshot;
        var target = request.Span.GetSpan(before);

        if (!string.Equals(target.GetText(), request.ExpectedText, StringComparison.Ordinal))
        {
            return NativeSnippetInsertionResult.FailedWithoutChange;
        }

        var adapter = _adapters.GetBufferAdapter(request.Buffer);

        if (adapter is not IVsTextLines || adapter is not IVsExpansion expansion)
        {
            return NativeSnippetInsertionResult.FailedWithoutChange;
        }

        EndCurrent(leaveCaret: true);
        _buffer = request.Buffer;
        _snippet = request.Snippet;
        _formatPending = true;
        var span = ToTextSpan(target);
        SqlNativeSnippetDom? dom = null;
        IVsExpansionSession? session = null;

        try
        {
            dom = SqlNativeSnippetXmlBuilder.CreateNode(
                request.Snippet,
                SnapshotNewLine.Resolve(before, target.Start.Position));
            var result = expansion.InsertSpecificExpansion(
                dom.Node,
                span,
                _client,
                SqlLanguageService.Resolve(),
                pszRelativePath: null,
                out session);

            if (ErrorHandler.Failed(result) || session is null)
            {
                var changed = !ReferenceEquals(before, request.Buffer.CurrentSnapshot);
                EndCurrent(leaveCaret: true);
                changed |= !ReferenceEquals(before, request.Buffer.CurrentSnapshot);
                return changed
                    ? NativeSnippetInsertionResult.FailedAfterChange
                    : NativeSnippetInsertionResult.FailedWithoutChange;
            }

            // 引擎已經在 OnBeforeInsertion／OnAfterInsertion 回呼裡給過同一個 session，
            // 這裡只是補上「引擎沒有回呼就成功返回」的情形。
            _session = session;
            SqlAssistRuntimeState.MarkActivity(SqlAssistActivityKind.SnippetExpanded);
            SqlAssistDiagnostics.Write($"已啟動原生 Snippet：{request.Snippet.Shortcut}");

            // 引擎已經把游標放進第一格，這一格要什麼由重開的那次分析決定。
            ReopenForCurrentField();
            return NativeSnippetInsertionResult.Succeeded;
        }
        finally
        {
            dom?.Dispose();
        }
    }

    public static void InsertFallback(ITextView textView, SqlSnippetExpansionRequest request)
    {
        var expansion = request.Snippet.Expansion;
        new TextViewEditCoordinator(textView, request.Buffer).ReplaceTracked(
            request.Span,
            "Snippet 降級內容",
            target =>
            {
                if (!string.Equals(target.GetText(), request.ExpectedText, StringComparison.Ordinal))
                {
                    return null;
                }

                var text = expansion.GetText(
                    SnapshotNewLine.Resolve(target.Snapshot, target.Start.Position),
                    out var caretOffset);
                return new TextReplacement(
                    text,
                    SqlAssistActivityKind.SnippetExpanded,
                    $"原生 Snippet 不可用，已以游標模式展開：{request.Snippet.Shortcut}",
                    caretOffset);
            });
    }

    public bool MoveNext()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var session = _session;

        if (session is null)
        {
            return false;
        }

        if (ErrorHandler.Failed(session.GoToNextExpansionField(fCommitIfLast: 1)))
        {
            EndCurrent(leaveCaret: true);
            return true;
        }

        // 最後一格的 Tab 會提交整個 session（fCommitIfLast），那時已經沒有欄位；
        // EndExpansion 的回呼在上面那個呼叫裡就已經把 _session 清掉了。
        if (_session is not null)
        {
            ReopenForCurrentField();
        }

        return true;
    }

    public bool MovePrevious()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var session = _session;

        if (session is null)
        {
            return false;
        }

        if (ErrorHandler.Failed(session.GoToPreviousExpansionField()))
        {
            EndCurrent(leaveCaret: true);
            return true;
        }

        if (_session is not null)
        {
            ReopenForCurrentField();
        }

        return true;
    }

    /// <summary>
    /// 游標落在原生 Snippet 的哪一格裡。
    /// </summary>
    /// <remarks>
    /// 範圍一律向引擎要，<b>不要</b>從目前的選取或游標方向推。進入欄位時引擎會把
    /// 整格選起來，游標可能停在頭也可能停在尾，而使用者自己拖選一段之後 Selection
    /// 就完全不是欄位邊界了；只有引擎手上的那份標記會跟著每一次編輯移動。
    ///
    /// 引擎不提供「目前在哪一格」，也沒有列舉欄位的方法，只有
    /// <see cref="IVsExpansionSession.GetFieldSpan"/> 這個要名稱的查詢，所以名稱由
    /// <see cref="SqlSnippetExpansion.Fields"/> 供給、逐格比對。內建片段最多七格，
    /// 而且沒有 session 時第一行就走掉——一般編輯完全不會付這筆 COM 呼叫。
    ///
    /// <b>同名欄位只認得第一個實例</b>：<c>GetFieldSpan</c> 對重複出現的欄位只回一個
    /// 範圍。游標停在第二個實例時這裡回 null，那一格就沒有清單——是退化，不是錯誤，
    /// 而合併之後的物件欄位每一格都只出現一次。
    /// </remarks>
    public SqlSnippetFieldSpan? FindFieldSpan(SnapshotPoint point)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_session is not { } session || _snippet is not { } snippet)
        {
            return null;
        }

        var spans = new TextSpan[1];

        foreach (var field in snippet.Expansion.Fields)
        {
            if (ErrorHandler.Failed(session.GetFieldSpan(field.Placeholder.Id, spans)))
            {
                continue;
            }

            // 尾端也算在格內：Tab 進來時游標就停在那裡。
            if (ToSnapshotSpan(spans[0], point.Snapshot) is { } span &&
                point >= span.Start &&
                point <= span.End)
            {
                return new SqlSnippetFieldSpan(span, field.Placeholder.DefaultValue);
            }
        }

        return null;
    }

    /// <summary>游標所在格；不在任何一格裡就回 null。</summary>
    public static SqlSnippetFieldSpan? FindFieldSpan(ITextView textView, SnapshotPoint point)
    {
        return Peek(textView)?.FindFieldSpan(point);
    }

    /// <summary>
    /// 上下文分析要讀到哪裡為止。
    /// </summary>
    /// <remarks>
    /// 格子裡<b>還是樣板填的預設值</b>時截到這一格的起點，當那幾個字不存在：
    /// <c>dbo.TargetTable</c> 算進去的話，分析器會把 <c>TargetTable</c> 當成篩選
    /// 前綴、把 <c>dbo</c> 當成限定字，清單十之八九是空的——那正是 <c>tabStops</c>
    /// 過去不敢開清單的原因。
    ///
    /// 使用者一打字就<b>不再截斷</b>。他打的那幾個字就是前綴，而且對無限定字的
    /// 格子來說那是唯一的參與條件：<c>INSERT (|)</c> 推不出目標，一律截到起點的話
    /// 前綴永遠是空的，那一格就永遠不會有清單——與 <c>SELECT |</c> 打了字才出現
    /// 清單是同一條規則，欄位裡沒有理由不一樣。
    ///
    /// 兩個呼叫端（建議來源的適用範圍、提交時的插入文字）都要照同一條規則，
    /// 因此規則放在這裡而不是各寫一次三元運算。
    /// </remarks>
    public static int ResolveAnalysisEnd(SqlSnippetFieldSpan? fieldSpan, int fallback)
    {
        return fieldSpan is { HoldsDefault: true } field ? field.Span.Start.Position : fallback;
    }

    public bool EndForEscape()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_session is null)
        {
            return false;
        }

        EndCurrent(leaveCaret: true);
        return true;
    }

    public bool EndForEnter()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_session is null)
        {
            return false;
        }

        // Enter 的語意維持編輯器換行；只先結束欄位追蹤，不把它改成另一顆 Tab。
        EndCurrent(leaveCaret: true);
        return true;
    }

    public void OnBeforeInsertion(IVsExpansionSession session) => _session = session;

    public void OnAfterInsertion(IVsExpansionSession session) => _session = session;

    /// <summary>引擎自己結束 session 時的回呼；放掉參考就好。</summary>
    /// <remarks>
    /// 這個回呼會發生在 <c>EndCurrentExpansion</c>／<c>GoToNextExpansionField</c>
    /// 還沒返回的 COM 呼叫堆疊裡，所以這裡不能做任何會影響那個呼叫的事。
    /// </remarks>
    public void OnEndExpansion()
    {
        _session = null;
        _buffer = null;
        _snippet = null;
        _formatPending = false;
    }

    /// <summary>
    /// 把插入點所在行的縮排補到片段的後續每一行。
    /// </summary>
    /// <remarks>
    /// 引擎<b>不會</b>自己縮排：它把 <c>Code</c> 逐字插進去，第 2 行之後一律從第 0 欄
    /// 開始。<c>FormatSpan</c> 是唯一的補救點，而回報 S_OK 卻什麼都不做等於告訴引擎
    /// 「已經排好了」——<c>trn</c>、<c>cur</c>、<c>ctb</c> 這些多行片段插在縮排位置時
    /// 就會整段貼齊左邊。
    ///
    /// 只補「插入點那一行的前導空白」而不做真正的 T-SQL 格式化：片段自己的相對縮排
    /// 已經寫在 <c>Code</c> 裡，這裡要加的只是整段的基準。空白也直接複製那一行的，
    /// 使用者用 Tab 還是空格自然會一致。
    /// </remarks>
    public void FormatSpan(TextSpan span)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!_formatPending || _buffer is not { } buffer || _textView.IsClosed)
        {
            return;
        }

        _formatPending = false;
        var snapshot = buffer.CurrentSnapshot;

        if (span.iStartLine < 0 ||
            span.iEndLine <= span.iStartLine ||
            span.iEndLine >= snapshot.LineCount)
        {
            return;
        }

        var indent = LeadingWhitespace(
            snapshot.GetLineFromLineNumber(span.iStartLine).GetText(),
            span.iStartIndex);

        if (indent.Length == 0)
        {
            return;
        }

        // 一次編輯涵蓋所有行：分次套用會讓引擎的欄位標記各追蹤一輪，
        // 也會在復原堆疊上留下好幾格。
        using var edit = buffer.CreateEdit();

        for (var number = span.iStartLine + 1; number <= span.iEndLine; number++)
        {
            var line = snapshot.GetLineFromLineNumber(number);

            // 空白行不補：那只會變成一行看不見的尾隨空白，
            // 而且下一次存檔又會被編輯器刪掉，diff 多出無意義的變動。
            if (line.Length > 0)
            {
                edit.Insert(line.Start.Position, indent);
            }
        }

        edit.Apply();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _textView.Closed -= OnTextViewClosed;
        EndCurrent(leaveCaret: true);
    }

    /// <summary>
    /// 結束目前的 session。
    /// </summary>
    /// <remarks>
    /// <b>刻意不呼叫 <c>Marshal.ReleaseComObject</c>。</b>
    /// <see cref="IVsExpansionSession"/> 的 RCW 是殼層自己也持有的那一個
    /// （CLR 的 RCW 快取讓同一個 COM 物件只對應一個 RCW），手動遞減計數之後，
    /// 殼層下一次用到它就會拿到 <c>InvalidComObjectException</c>——而且發生的時機
    /// 取決於誰先用到，看起來像隨機的當掉。
    ///
    /// 先把欄位設 null 再呼叫 <c>EndCurrentExpansion</c>：那個呼叫會同步回呼
    /// <see cref="OnEndExpansion"/>，先清掉才不會在回呼裡看到一個正在結束的 session。
    /// </remarks>
    private void EndCurrent(bool leaveCaret)
    {
        var session = _session;
        _session = null;
        _buffer = null;
        _snippet = null;
        _formatPending = false;

        if (session is not null)
        {
            _ = session.EndCurrentExpansion(leaveCaret ? 1 : 0);
        }
    }

    /// <summary>進入某一格之後把建議清單重開一次。</summary>
    /// <remarks>
    /// 刻意<b>不</b>先判斷「這一格該不該有清單」。重開本來就會經過
    /// <c>SqlCompletionContextAnalyzer</c>，那裡不參與時平台什麼都不會顯示——
    /// <c>CREATE TABLE $table$</c> 那種新名字的格子因此自己就安靜了。在這裡先猜
    /// 一次等於把同一條規則寫成兩份，而分岔的症狀是「有些格子該跳清單卻不跳」。
    ///
    /// 不在原地開：這個方法的三個呼叫點都還在引擎的 COM 呼叫堆疊裡，
    /// 文字與游標都還沒定案。排程與三步驟重開都沿用
    /// <see cref="SqlCompletionReopen.AfterExpansion"/>。
    /// </remarks>
    private void ReopenForCurrentField()
    {
        SqlCompletionReopen.AfterExpansion(_textView, _broker);
    }

    /// <summary>把引擎的行列範圍換算成快照範圍；換不出來時回 null。</summary>
    /// <remarks>
    /// 每一個邊界都要自己驗：範圍來自引擎，快照是我們自己取的，兩者之間只要有一次
    /// 不同步，<see cref="Span.FromBounds"/> 就會丟例外——而這條路徑在按鍵上，
    /// 一次例外就是使用者按一次鍵看到一次錯誤對話框。
    /// </remarks>
    private static SnapshotSpan? ToSnapshotSpan(TextSpan span, ITextSnapshot snapshot)
    {
        if (span.iStartLine < 0 ||
            span.iEndLine < span.iStartLine ||
            span.iEndLine >= snapshot.LineCount)
        {
            return null;
        }

        var startLine = snapshot.GetLineFromLineNumber(span.iStartLine);
        var endLine = snapshot.GetLineFromLineNumber(span.iEndLine);

        if (span.iStartIndex < 0 ||
            span.iStartIndex > startLine.Length ||
            span.iEndIndex < 0 ||
            span.iEndIndex > endLine.Length)
        {
            return null;
        }

        var start = startLine.Start.Position + span.iStartIndex;
        var end = endLine.Start.Position + span.iEndIndex;

        return end < start
            ? null
            : new SnapshotSpan(snapshot, Span.FromBounds(start, end));
    }

    /// <summary>取一行的前導空白，最多取到片段的起始欄。</summary>
    /// <remarks>
    /// 夾在起始欄是為了「片段起點落在前導空白之內」這種情形：
    /// 整行縮排 8 格但游標停在第 4 欄時，補 8 格會把後續行推得比第一行還深。
    /// </remarks>
    private static string LeadingWhitespace(string line, int startIndex)
    {
        var length = 0;

        while (length < line.Length && length < startIndex && char.IsWhiteSpace(line[length]))
        {
            length++;
        }

        return length == 0 ? string.Empty : line.Substring(0, length);
    }

    private void OnTextViewClosed(object sender, EventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("關閉原生 Snippet session", Dispose);
    }

    private static TextSpan ToTextSpan(SnapshotSpan span)
    {
        var startLine = span.Snapshot.GetLineFromPosition(span.Start.Position);
        var endLine = span.Snapshot.GetLineFromPosition(span.End.Position);

        return new TextSpan
        {
            iStartLine = startLine.LineNumber,
            iStartIndex = span.Start.Position - startLine.Start.Position,
            iEndLine = endLine.LineNumber,
            iEndIndex = span.End.Position - endLine.Start.Position
        };
    }

}

/// <summary>
/// 原生 Expansion Engine 呼叫回 SqlAssist 的平台邊界。
/// </summary>
/// <remarks>
/// 只有真的會做事的三個方法（<see cref="FormatSpan"/>、<see cref="EndExpansion"/>、
/// 兩個插入回呼）走 <see cref="SqlAssistPlatformGuard"/>。其餘幾個的內容只是回一個
/// 常數，包起來只是儀式——Guard 是用來擋住「會丟例外的程式碼」的，套在丟不出
/// 例外的地方會讓真正需要它的那幾處看起來沒有特別之處。
/// </remarks>
internal sealed class SqlSnippetExpansionClient : IVsExpansionClient
{
    private readonly SqlSnippetExpansionController _controller;

    public SqlSnippetExpansionClient(SqlSnippetExpansionController controller)
    {
        _controller = controller;
    }

    /// <summary>不提供自訂函式；<c>$函式()$</c> 那一套語法用不到。</summary>
    public int GetExpansionFunction(IXMLDOMNode xmlFunctionNode, string fieldName, out IVsExpansionFunction function)
    {
        function = null!;
        return VSConstants.E_NOTIMPL;
    }

    /// <remarks>
    /// 只看第一個範圍：引擎在插入時交的就是整個片段的範圍。
    ///
    /// 失敗一律回 S_OK：縮排沒補上只是不好看，回報失敗會讓引擎把整個插入視為失敗，
    /// 而那時文字已經在緩衝區裡了。
    /// </remarks>
    public int FormatSpan(IVsTextLines textLines, TextSpan[] spans)
    {
        if (spans is { Length: > 0 })
        {
            SqlAssistPlatformGuard.Run(
                "格式化原生 Snippet",
                () => _controller.FormatSpan(spans[0]));
        }

        return VSConstants.S_OK;
    }

    public int EndExpansion()
    {
        return SqlAssistPlatformGuard.Run(
            "結束原生 Snippet session",
            () =>
            {
                _controller.OnEndExpansion();
                return VSConstants.S_OK;
            },
            VSConstants.E_FAIL);
    }

    /// <summary>型別與種類一律放行：XML 是我們自己從內建定義組出來的。</summary>
    public int IsValidType(
        IVsTextLines textLines,
        TextSpan[] spans,
        string[] types,
        int typeCount,
        out int isValid)
    {
        isValid = 1;
        return VSConstants.S_OK;
    }

    public int IsValidKind(
        IVsTextLines textLines,
        TextSpan[] spans,
        string kind,
        out int isValid)
    {
        isValid = 1;
        return VSConstants.S_OK;
    }

    public int OnBeforeInsertion(IVsExpansionSession session)
    {
        return SqlAssistPlatformGuard.Run(
            "原生 Snippet 插入前回呼",
            () =>
            {
                _controller.OnBeforeInsertion(session);
                return VSConstants.S_OK;
            },
            VSConstants.E_FAIL);
    }

    public int OnAfterInsertion(IVsExpansionSession session)
    {
        return SqlAssistPlatformGuard.Run(
            "原生 Snippet 插入後回呼",
            () =>
            {
                _controller.OnAfterInsertion(session);
                return VSConstants.S_OK;
            },
            VSConstants.E_FAIL);
    }

    /// <summary>游標由引擎自己放到第一個欄位，這裡不介入。</summary>
    public int PositionCaretForEditing(IVsTextLines textLines, TextSpan[] spans) => VSConstants.S_OK;

    /// <summary>只有引擎自己的挑選介面會用到；SqlAssist 一律從建議清單提交。</summary>
    public int OnItemChosen(string title, string path) => VSConstants.S_OK;
}
