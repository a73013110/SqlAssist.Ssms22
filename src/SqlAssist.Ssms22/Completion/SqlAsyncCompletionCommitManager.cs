using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using SqlAssist.Core;

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

    public SqlAsyncCompletionCommitManager(SqlModuleExpander moduleExpander)
    {
        _moduleExpander = moduleExpander;
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

            var snapshot = buffer.CurrentSnapshot;
            var span = session.ApplicableToSpan.GetSpan(snapshot);
            var context = SqlCompletionContextAnalyzer.Analyze(snapshot.GetText(), span.End);
            var settings = SettingsService.Default.GetSnapshot();
            var expansionSpan = SqlModuleExpander.TryCreateStatementSpan(
                suggestion,
                context,
                snapshot,
                span.End);

            // 一般項目讓平台自己插入，行為與其他語言一致。
            if (expansionSpan is null && !suggestion.TriggerFollowUp)
            {
                return CommitResult.Unhandled;
            }

            var insertionText = SqlInsertionText.Build(suggestion, context, settings);

            using (var edit = buffer.CreateEdit())
            {
                edit.Replace(span, insertionText);

                if (edit.Apply() is null || edit.Canceled)
                {
                    return CommitResult.Unhandled;
                }
            }

            SqlAssistRuntimeState.MarkExpansion(insertionText.TrimEnd());
            SqlAssistDiagnostics.Write($"Suggestion 已提交：{suggestion.DisplayText}");

            if (expansionSpan is not null && suggestion.Tag is Metadata.SqlObjectInfo objectInfo)
            {
                _moduleExpander.Begin(objectInfo, expansionSpan);
                return new CommitResult(isHandled: true, CommitBehavior.None);
            }

            // 接續建議：ssf 展開成 SELECT * FROM 之後直接列出資料表與檢視。
            SqlAssistDiagnostics.WriteAlways(
                $"已進入接續建議：{suggestion.DisplayText}，下一步只顯示對應資料庫物件");
            return new CommitResult(isHandled: true, CommitBehavior.Retrigger);
        }
        catch (Exception exception)
        {
            // 提交失敗不可以讓按鍵處理鏈整個炸掉；交還給平台用預設方式插入。
            SqlAssistDiagnostics.WriteAlways($"提交建議失敗：{exception}");
            return CommitResult.Unhandled;
        }
    }
}
