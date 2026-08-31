using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using MSXML;
using SqlAssist.Core.Snippets;
using SqlAssist.Ssms22.Editor;

namespace SqlAssist.Ssms22.Snippets;

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
    private readonly SqlSnippetExpansionClient _client;
    private IVsExpansionSession? _session;

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
        IVsEditorAdaptersFactoryService adapters)
    {
        _textView = textView;
        _adapters = adapters;
        _client = new SqlSnippetExpansionClient(this);
        _textView.Closed += OnTextViewClosed;
    }

    public bool HasActiveSession => _session is not null;

    public static void Attach(IWpfTextView textView, IVsEditorAdaptersFactoryService adapters)
    {
        textView.Properties.GetOrCreateSingletonProperty(
            PropertyKey,
            () => new SqlSnippetExpansionController(textView, adapters));
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
            SqlAssistRuntimeState.MarkExpansion(request.Snippet.Title);
            SqlAssistDiagnostics.Write($"已啟動原生 Snippet：{request.Snippet.Shortcut}");
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
                    request.Snippet.Title,
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
        }

        return true;
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
        _formatPending = false;

        if (session is not null)
        {
            _ = session.EndCurrentExpansion(leaveCaret ? 1 : 0);
        }
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

/// <summary>原生 Expansion Engine 呼叫回 SqlAssist 的平台邊界。</summary>
internal sealed class SqlSnippetExpansionClient : IVsExpansionClient
{
    private readonly SqlSnippetExpansionController _controller;

    public SqlSnippetExpansionClient(SqlSnippetExpansionController controller)
    {
        _controller = controller;
    }

    public int GetExpansionFunction(IXMLDOMNode xmlFunctionNode, string fieldName, out IVsExpansionFunction function)
    {
        function = null!;
        return SqlAssistPlatformGuard.Run(
            "查詢原生 Snippet 函式",
            () => VSConstants.E_NOTIMPL,
            VSConstants.E_FAIL);
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

    public int IsValidType(
        IVsTextLines textLines,
        TextSpan[] spans,
        string[] types,
        int typeCount,
        out int isValid)
    {
        isValid = 1;
        return SqlAssistPlatformGuard.Run(
            "驗證原生 Snippet 類型",
            () => VSConstants.S_OK,
            VSConstants.E_FAIL);
    }

    public int IsValidKind(
        IVsTextLines textLines,
        TextSpan[] spans,
        string kind,
        out int isValid)
    {
        isValid = 1;
        return SqlAssistPlatformGuard.Run(
            "驗證原生 Snippet 種類",
            () => VSConstants.S_OK,
            VSConstants.E_FAIL);
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

    public int PositionCaretForEditing(IVsTextLines textLines, TextSpan[] spans) =>
        SqlAssistPlatformGuard.Run("定位原生 Snippet 游標", () => VSConstants.S_OK, VSConstants.E_FAIL);

    public int OnItemChosen(string title, string path) =>
        SqlAssistPlatformGuard.Run("選取原生 Snippet 項目", () => VSConstants.S_OK, VSConstants.E_FAIL);
}
