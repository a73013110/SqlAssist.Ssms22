using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using SqlAssist.Core;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 新版非同步 IntelliSense 的探測用建議來源。
/// </summary>
/// <remarks>
/// 預設<b>不參與</b>完成流程：<see cref="InitializeCompletion"/> 只記錄自己有沒有被呼叫，
/// 然後回報不參與，因此 SSMS 原生 IntelliSense 的行為不受影響。
/// 只有在設定開啟 <c>asyncCompletionProbe</c> 之後才會真的提供項目，
/// 用來觀察清單外觀、Tab 提交與原生清單的互動。
/// </remarks>
internal sealed class SqlAsyncCompletionSource : IAsyncCompletionSource
{
    private static readonly ImmutableArray<SqlSuggestion> BuiltIn =
        BuiltInSuggestionCatalog.Create().ToImmutableArray();

    public CompletionStartData InitializeCompletion(
        CompletionTrigger trigger,
        SnapshotPoint triggerLocation,
        CancellationToken token)
    {
        try
        {
            AsyncCompletionProbe.RecordInitialize($"{trigger.Reason} '{trigger.Character}'");

            if (!SettingsService.Default.GetSnapshot().AsyncCompletionProbe)
            {
                return CompletionStartData.DoesNotParticipateInCompletion;
            }

            var context = AnalyzeAt(triggerLocation);

            if (!context.IsValid)
            {
                return CompletionStartData.DoesNotParticipateInCompletion;
            }

            AsyncCompletionProbe.RecordParticipation();

            var applicableSpan = new SnapshotSpan(
                triggerLocation.Snapshot,
                Span.FromBounds(context.TokenStart, triggerLocation.Position));

            return new CompletionStartData(CompletionParticipation.ProvidesItems, applicableSpan);
        }
        catch (Exception exception)
        {
            AsyncCompletionProbe.RecordError(exception);
            return CompletionStartData.DoesNotParticipateInCompletion;
        }
    }

    public Task<CompletionContext> GetCompletionContextAsync(
        IAsyncCompletionSession session,
        CompletionTrigger trigger,
        SnapshotPoint triggerLocation,
        SnapshotSpan applicableToSpan,
        CancellationToken token)
    {
        try
        {
            var context = AnalyzeAt(triggerLocation);
            var matches = SuggestionMatcher.Rank(BuiltIn, context, maximumCount: 50);
            var items = matches
                .Select(match => new CompletionItem(match.Suggestion.DisplayText, this))
                .ToImmutableArray();

            AsyncCompletionProbe.RecordContext(items.Length);
            return Task.FromResult(new CompletionContext(items));
        }
        catch (Exception exception)
        {
            AsyncCompletionProbe.RecordError(exception);
            return Task.FromResult(CompletionContext.Empty);
        }
    }

    public Task<object> GetDescriptionAsync(
        IAsyncCompletionSession session,
        CompletionItem item,
        CancellationToken token)
    {
        AsyncCompletionProbe.RecordDescription();

        var suggestion = BuiltIn.FirstOrDefault(candidate =>
            string.Equals(candidate.DisplayText, item.DisplayText, StringComparison.Ordinal));

        return Task.FromResult<object>(suggestion?.Preview ?? item.DisplayText);
    }

    private static SqlCompletionContext AnalyzeAt(SnapshotPoint triggerLocation)
    {
        // 與自製清單走同一條分析路徑，含別名解析；探測要能反映真實行為才有意義。
        return SqlCompletionContextAnalyzer.Analyze(
            triggerLocation.Snapshot.GetText(),
            triggerLocation.Position);
    }
}
