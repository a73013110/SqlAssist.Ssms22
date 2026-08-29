using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Snippets;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 決定原生建議清單的提交行為。
/// </summary>
/// <remarks>
/// 大部分項目交還給平台處理即可（它會插入 <see cref="CompletionItem.InsertText"/>），
/// 只有兩種情形要自己接手：
/// 接續建議（<c>ssf</c> 展開後要立刻列出資料表），以及
/// <c>ALTER PROCEDURE</c> 之後要放進完整定義。
/// </remarks>
internal sealed class SqlAsyncCompletionCommitManager : IAsyncCompletionCommitManager
{
    private readonly SqlModuleExpander _moduleExpander;
    private readonly IAsyncCompletionBroker? _broker;

    public SqlAsyncCompletionCommitManager(
        SqlModuleExpander moduleExpander,
        IAsyncCompletionBroker? broker)
    {
        _moduleExpander = moduleExpander;
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
        try
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
            var context = SqlCompletionContextAnalyzer.Analyze(snapshot.GetText(), span.End);
            var settings = SqlAssistSettingsStore.Current;
            var shouldExpand = SqlModuleExpander.ShouldExpand(suggestion, context, span.End);

            // Snippet 要自己插入才放得下游標：$end$ 決定的位置不是文字結尾。
            var snippet = suggestion.Tag as SqlSnippet;
            var snippetCaret = -1;

            if (snippet is not null)
            {
                var expanded = snippet.Expand(out var caretOffset);

                if (caretOffset != expanded.Length)
                {
                    snippetCaret = caretOffset;
                }
            }

            // 一般項目讓平台自己插入，行為與其他語言一致。
            if (!shouldExpand && !suggestion.TriggerFollowUp && snippetCaret < 0)
            {
                return CommitResult.Unhandled;
            }

            var insertionText = SqlInsertionText.Build(suggestion, context, settings);
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

            SqlAssistRuntimeState.MarkExpansion(insertionText.TrimEnd());
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

            if (shouldExpand && suggestion.Tag is SqlObjectInfo objectInfo)
            {
                // 範圍要等名稱插好之後才建立，否則會漏掉剛插進去的名稱——理由寫在
                // SqlModuleExpander.CreateStatementSpan。起點與終點都以這次編輯的
                // 結果算：取代的起點不會位移，終點就是插入文字的結尾。
                var statementSpan = SqlModuleExpander.CreateStatementSpan(
                    applied,
                    context.TargetKeywordStart,
                    insertionStart + insertionText.Length);

                _moduleExpander.Begin(objectInfo, statementSpan);
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
        catch (Exception exception)
        {
            // 提交失敗不可以讓按鍵處理鏈整個炸掉；交還給平台用預設方式插入。
            SqlAssistDiagnostics.WriteAlways($"提交建議失敗：{exception}");
            return CommitResult.Unhandled;
        }
    }
}
