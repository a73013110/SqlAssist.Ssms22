using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using SqlAssist.Core;
using SqlAssist.Core.Matching;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 決定原生建議清單的排序、篩選與命中標示。
/// </summary>
/// <remarks>
/// 沒有這個匯出，平台會用自己的比對器，詞首感知排名就會失效——
/// 輸入 <c>libr</c> 時 <c>Lib_Reader</c> 又會掉到含子字串的名稱後面。
/// 這裡改用本擴充的模糊比對器，並把命中區段交給平台去畫粗體。
///
/// 同時實作新舊兩版介面：平台優先呼叫
/// <see cref="IAsyncCompletionItemManager2"/> 的清單版本以避免多一次陣列複製，
/// 舊版仍必須存在才能完成 MEF 契約。
/// </remarks>
internal sealed class SqlAsyncCompletionItemManager : IAsyncCompletionItemManager, IAsyncCompletionItemManager2
{
    /// <summary>
    /// 初始順序原樣保留。
    /// </summary>
    /// <remarks>
    /// 這只是「還沒輸入任何字元」時的顯示順序；一旦有前綴就完全由分數決定，
    /// 因此沒有必要在這裡再排一次。
    /// </remarks>
    public Task<CompletionList<CompletionItem>> SortCompletionItemListAsync(
        IAsyncCompletionSession session,
        AsyncCompletionSessionInitialDataSnapshot data,
        CancellationToken token)
    {
        return Task.FromResult(data.InitialItemList);
    }

    public Task<ImmutableArray<CompletionItem>> SortCompletionListAsync(
        IAsyncCompletionSession session,
        AsyncCompletionSessionInitialDataSnapshot data,
        CancellationToken token)
    {
        return Task.FromResult(data.InitialItemList.ToImmutableArray());
    }

    public Task<FilteredCompletionModel?> UpdateCompletionListAsync(
        IAsyncCompletionSession session,
        AsyncCompletionSessionDataSnapshot data,
        CancellationToken token)
    {
        try
        {
            return Task.FromResult(Filter(session, data, token));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 這裡丟出例外會讓整個 session 掛掉；退回不篩選的完整清單。
            SqlAssistDiagnostics.WriteAlways($"建議清單篩選失敗：{exception}");
            return Task.FromResult<FilteredCompletionModel?>(Passthrough(data));
        }
    }

    private static FilteredCompletionModel? Filter(
        IAsyncCompletionSession session,
        AsyncCompletionSessionDataSnapshot data,
        CancellationToken token)
    {
        var items = data.InitialSortedItemList;
        var pattern = FuzzyMatcher.NormalizePattern(GetTypedText(session, data));

        if (pattern.Length == 0)
        {
            return Passthrough(data);
        }

        var maximumItems = Math.Max(
            1,
            Math.Min(500, SettingsService.Default.GetSnapshot().Suggestions.MaximumItems));
        var scored = new List<ScoredItem>(items.Count);

        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            var match = FuzzyMatcher.MatchNormalized(pattern, item.DisplayText);

            if (!match.IsMatch)
            {
                continue;
            }

            scored.Add(new ScoredItem(item, ComposeScore(item, match, pattern), match.Spans));
        }

        // 一個都沒中時回傳 null，平台會關閉 session，
        // 而不是留一份空清單擋在游標旁邊。
        if (scored.Count == 0)
        {
            return null;
        }

        var filtered = scored
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Item.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Take(maximumItems)
            .Select(entry => new CompletionItemWithHighlight(entry.Item, ToSpans(entry.Spans)))
            .ToImmutableArray();

        return new FilteredCompletionModel(filtered, 0);
    }

    private static FilteredCompletionModel Passthrough(AsyncCompletionSessionDataSnapshot data)
    {
        return new FilteredCompletionModel(
            data.InitialSortedItemList.Select(item => new CompletionItemWithHighlight(item)).ToImmutableArray(),
            0);
    }

    /// <summary>取得使用者在建議範圍內已經輸入的文字。</summary>
    private static string GetTypedText(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data)
    {
        var span = session.ApplicableToSpan;

        return span is null ? string.Empty : span.GetText(data.Snapshot);
    }

    /// <summary>
    /// 合成最終排名分數。
    /// </summary>
    /// <remarks>
    /// 與自製清單共用 <see cref="SuggestionMatcher.ComposeScore"/>，
    /// 讓兩種引擎的排序結果一致。
    /// </remarks>
    private static int ComposeScore(CompletionItem item, FuzzyMatchResult match, string pattern)
    {
        return item.Properties.TryGetProperty<SqlSuggestion>(
            SqlAsyncCompletionSource.SuggestionKey,
            out var suggestion) && suggestion is not null
            ? SuggestionMatcher.ComposeScore(suggestion, match, pattern)
            : match.Score * 128;
    }

    private static ImmutableArray<Span> ToSpans(IReadOnlyList<MatchSpan> spans)
    {
        if (spans.Count == 0)
        {
            return ImmutableArray<Span>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<Span>(spans.Count);

        foreach (var span in spans)
        {
            builder.Add(new Span(span.Start, span.Length));
        }

        return builder.MoveToImmutable();
    }

    private readonly struct ScoredItem
    {
        public ScoredItem(CompletionItem item, int score, IReadOnlyList<MatchSpan> spans)
        {
            Item = item;
            Score = score;
            Spans = spans;
        }

        public CompletionItem Item { get; }

        public int Score { get; }

        public IReadOnlyList<MatchSpan> Spans { get; }
    }
}
