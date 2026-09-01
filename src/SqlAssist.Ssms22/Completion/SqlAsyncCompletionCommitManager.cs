using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Core.Snippets;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.Snippets;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 決定原生建議清單的提交行為。
/// </summary>
/// <remarks>
/// 大部分項目交還給平台處理即可（它會插入 <see cref="CompletionItem.InsertText"/>），
/// 只有三種情形要自己接手：原生 Tab Stop Snippet、設定了接續建議的 caret Snippet，
/// 以及提交後要把整個語句換掉的三種展開——<c>ALTER PROCEDURE</c> 的完整定義、
/// <c>INSERT INTO</c> 的欄位與 <c>VALUES</c>、<c>EXEC</c> 的具名參數清單。
/// 後三種只有「換成什麼」不同，「怎麼安全地換」共用 <see cref="SqlCommitExpander"/>。
/// </remarks>
internal sealed class SqlAsyncCompletionCommitManager : IAsyncCompletionCommitManager
{
    private readonly SqlCommitExpander _commitExpander;
    private readonly IAsyncCompletionBroker? _broker;

    public SqlAsyncCompletionCommitManager(
        SqlCommitExpander commitExpander,
        IAsyncCompletionBroker? broker)
    {
        _commitExpander = commitExpander;
        _broker = broker;
    }

    /// <summary>
    /// 除了 Tab 與 Enter（平台一律視為提交）之外，沒有任何字元會提交。
    /// </summary>
    /// <remarks>
    /// 曾經把點號列為提交字元，想讓 <c>dbo.</c> 自動補完結構描述名稱，
    /// 但這會把使用者根本不想要的項目寫進編輯器：
    /// 在 <c>SELECT | FROM PUBLISHER a</c> 輸入 <c>a</c> 再輸入 <c>.</c> 時，
    /// 清單選中的是分數最高的 Snippet <c>af</c>，於是變成
    /// <c>SELECT ALTER FUNCTION . FROM PUBLISHER a</c>。
    ///
    /// 這不是排名問題：使用者輸入 <c>a.</c> 是要引用別名 <c>a</c>，
    /// 當下沒有任何項目該被提交，選中的若是資料表 <c>abc</c> 也一樣是錯的。
    /// 點號本來就會讓分析器重新判斷上下文並開新的 session（別名接欄位、
    /// 結構描述接物件），不需要靠提交來達成。
    /// </remarks>
    public IEnumerable<char> PotentialCommitCharacters { get; } = Array.Empty<char>();

    public bool ShouldCommitCompletion(
        IAsyncCompletionSession session,
        SnapshotPoint location,
        char typedChar,
        CancellationToken token)
    {
        return false;
    }

    public CommitResult TryCommit(
        IAsyncCompletionSession session,
        ITextBuffer buffer,
        CompletionItem item,
        char typedChar,
        CancellationToken token)
    {
        // 提交失敗不可以讓按鍵處理鏈整個炸掉；交還給平台用預設方式插入。
        return SqlAssistPlatformGuard.Run(
            "提交建議",
            () => TryCommitCore(session, buffer, item, typedChar),
            fallback: CommitResult.Unhandled);
    }

    private CommitResult TryCommitCore(
        IAsyncCompletionSession session,
        ITextBuffer buffer,
        CompletionItem item,
        char typedChar)
    {
        if (!item.Properties.TryGetProperty<SqlSuggestion>(
                SqlAsyncCompletionSource.SuggestionKey,
                out var suggestion))
        {
            return CommitResult.Unhandled;
        }

        // 排名要記住這一筆。提交路徑上大部分項目最後會交還給平台
        // （下面那個 Unhandled），所以必須記在早退之前，否則只有
        // Snippet 與模組展開這兩種特例會被記住。
        SqlSuggestionUsage.Record(suggestion);

        var snapshot = buffer.CurrentSnapshot;
        var span = session.ApplicableToSpan.GetSpan(snapshot);
        var snippet = suggestion.Tag as SqlSnippet;

        if (snippet is { ExpansionMode: SqlSnippetExpansionMode.TabStops })
        {
            // 提交當下平台還在關閉 Completion session。此處只保留追蹤範圍，
            // 等 Dispatcher Background 再讓原生引擎一次完成移除捷徑與插入片段。
            var request = new SqlSnippetExpansionRequest(
                buffer,
                snapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeInclusive),
                span.GetText(),
                snippet);

            // 這次提交已回報 handled，即使使用者剛好在同一瞬間關掉建議設定也必須完成；
            // 走 SqlCompletionReopen 的設定守門會把捷徑原樣留在編輯器裡。
            TextViewDispatch.AfterCurrentCommand(session.TextView, "展開原生 Snippet", view =>
            {
                var controller = SqlSnippetExpansionController.Peek(view);
                var beforeNative = request.Buffer.CurrentSnapshot;
                var result = controller is null
                    ? NativeSnippetInsertionResult.FailedWithoutChange
                    : SqlAssistPlatformGuard.Run(
                        "呼叫原生 Snippet Expansion",
                        () => controller.TryInsert(request),
                        NativeSnippetInsertionResult.FailedWithoutChange);

                if (result == NativeSnippetInsertionResult.FailedWithoutChange &&
                    !ReferenceEquals(beforeNative, request.Buffer.CurrentSnapshot))
                {
                    result = NativeSnippetInsertionResult.FailedAfterChange;
                }

                if (result == NativeSnippetInsertionResult.FailedWithoutChange)
                {
                    SqlSnippetExpansionController.InsertFallback(view, request);
                }
                else if (result == NativeSnippetInsertionResult.FailedAfterChange)
                {
                    // 引擎已改過緩衝區時再插 fallback 會重複內容；寧可保留它的結果並記錄。
                    SqlAssistDiagnostics.WriteAlways(
                        $"原生 Snippet 在回報失敗前已改動文字，已略過降級插入：{snippet.Shortcut}",
                        view);
                }
            });

            return new CommitResult(isHandled: true, CommitBehavior.None);
        }

        // 只看游標前文就夠：提交要的是限定字（決定要不要補結構描述）與語句的
        // 關鍵字起點，兩者都在游標之前。欄位的插入文字在建立建議時就定案了，
        // 這裡不必再解析一次別名。
        //
        // 在原生 Snippet 欄位裡則要截到這一格的起點：格子裡是樣板填的
        // dbo.TargetTable，把它算進來的話 dbo 會被當成限定字，插進去的名稱就少了
        // 結構描述。範圍重問一次而不是從建議來源傳過來——緩衝區在這中間可能已經
        // 變了，而這是整條路徑上唯一還來得及發現的地方。
        var fieldSpan = SqlSnippetExpansionController.FindFieldSpan(session.TextView, span.Start);
        var context = SqlCompletionContextAnalyzer.Analyze(
            snapshot.GetText(
                0,
                SqlSnippetExpansionController.ResolveAnalysisEnd(fieldSpan, span.End.Position)));

        // Tab 在欄位裡有兩件事要做：提交這一格，然後走到下一格。平台的 Tab 只做
        // 得了第一件，所以第二件排在這一輪命令之後自己做——不靠
        // CommitBehavior.RaiseFurtherReturnKeyAndTabKeyCommandHandlers 把命令鏈接
        // 下去，那要求本處理常式與平台的先後順序固定，而目前兩者都只寫 Before=default。
        //
        // 排程而不是原地呼叫：文字要等這次提交寫完才是最終狀態，而下一格的清單
        // 是由 MoveNext 自己重開的（見 SqlSnippetExpansionController）。
        //
        // 只認 Tab：Enter 在 session 裡的語意是換行並結束欄位追蹤，
        // 那一條寫在 SqlTabCommandHandler，這裡不改它。平台若在 Tab 提交時傳的
        // 不是 \t，退化成「再按一次 Tab 才跳格」，與改動前一樣。
        if (fieldSpan is not null && typedChar == '\t')
        {
            TextViewDispatch.AfterCurrentCommand(session.TextView, "提交後前往下一格", view =>
                SqlSnippetExpansionController.Peek(view)?.MoveNext());
        }
        var settings = SqlAssistSettingsStore.Current;
        var expansion = SqlCommitExpander.Resolve(suggestion, context, span.End, settings);

        // Snippet 要自己插入才放得下游標：$end$ 決定的位置不是文字結尾。
        var snippetCaret = -1;
        string? snippetText = null;

        if (snippet is not null)
        {
            // 多行片段要跟著這份檔案的換行。用 Expansion 的原文會把 LF 直接插進
            // 一份 CRLF 的指令碼裡，而混合換行不會報錯，只會讓下一次 diff 整段變紅。
            snippetText = snippet.Expansion.GetText(
                SnapshotNewLine.Resolve(snapshot, span.Start.Position),
                out var caretOffset);

            if (caretOffset != snippetText.Length)
            {
                snippetCaret = caretOffset;
            }
        }

        // 一般項目讓平台自己插入，行為與其他語言一致。
        if (expansion is null && !suggestion.TriggerFollowUp && snippetCaret < 0)
        {
            return CommitResult.Unhandled;
        }

        var insertionText = snippetText ?? SqlInsertionText.Build(suggestion, context, settings);
        var insertionStart = span.Start.Position;
        ITextSnapshot applied;

        using (var edit = buffer.CreateEdit())
        {
            edit.Replace(span, insertionText);
            var result = edit.Apply();

            if (result is null || edit.Canceled)
            {
                return CommitResult.Unhandled;
            }

            applied = result;
        }

        SqlAssistRuntimeState.MarkActivity(
            snippet is null
                ? SqlAssistActivityKind.SuggestionCommitted
                : SqlAssistActivityKind.SnippetExpanded);
        SqlAssistDiagnostics.Write($"Suggestion 已提交：{suggestion.DisplayText}");

        if (snippetCaret >= 0)
        {
            // 編輯已經套用，insertionStart 在新快照裡仍然有效——取代的起點不會位移。
            var caret = insertionStart + snippetCaret;
            var current = session.TextView.TextSnapshot;

            if (caret <= current.Length)
            {
                session.TextView.Caret.MoveTo(new SnapshotPoint(current, caret));
            }
        }

        if (expansion is not null)
        {
            // 範圍要等名稱插好之後才建立，否則會漏掉剛插進去的名稱——理由寫在
            // SqlCommitExpander.CreateStatementSpan。起點與終點都以這次編輯的
            // 結果算：取代的起點不會位移，終點就是插入文字的結尾。
            var statementSpan = SqlCommitExpander.CreateStatementSpan(
                applied,
                context.TargetKeywordStart,
                insertionStart + insertionText.Length);

            // 展開是另一次獨立的編輯，因此按一次復原就退回「只插入名稱」的狀態——
            // 想要 INSERT INTO t SELECT … 或照順序傳值的 EXEC 時走的就是那條路。
            _commitExpander.Begin(expansion, statementSpan, insertionText);
            return new CommitResult(isHandled: true, CommitBehavior.None);
        }

        // 沒有勾接續的片段到這裡就結束；文字已經插好，游標也已經就位。
        if (!suggestion.TriggerFollowUp)
        {
            return new CommitResult(isHandled: true, CommitBehavior.None);
        }

        // 接續建議：ssf 展開成 SELECT * FROM 之後直接列出資料表與檢視。
        //
        // 這裡刻意不回報 CommitBehavior.Retrigger。那個旗標在 SSMS 22 上是死的
        // ——編輯器組件裡沒有任何一處讀它，Enter 與 Tab 只測
        // RaiseFurtherReturnKeyAndTabKeyCommandHandlers，輸入字元只測
        // SuppressFurtherTypeCharCommandHandlers。回報一個不會有人看的旗標
        // 只會讓下一個讀這段程式的人以為接續是平台在做的。
        SqlAssistDiagnostics.WriteAlways(
            $"已進入接續建議：{suggestion.DisplayText}，下一步只顯示對應資料庫物件");
        SqlCompletionReopen.AfterExpansion(session.TextView, _broker);
        return new CommitResult(isHandled: true, CommitBehavior.None);
    }
}
