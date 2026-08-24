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
    /// 除了 Tab 與 Enter（平台一律視為提交）之外，還有哪些字元要提交。
    /// </summary>
    /// <remarks>
    /// 只放點號：輸入 <c>dbo.</c> 時應該提交 <c>dbo</c> 再列出該結構描述的物件。
    /// 逗號、空白與括號不列入，否則打字打到一半會被硬生生提交成不想要的項目。
    /// </remarks>
    public IEnumerable<char> PotentialCommitCharacters { get; } = new[] { '.' };

    public bool ShouldCommitCompletion(
        IAsyncCompletionSession session,
        SnapshotPoint location,
        char typedChar,
        CancellationToken token)
    {
        return typedChar == '.';
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
