using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Notifications;
using SqlAssist.Ssms22;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 把原生建議清單的排序、篩選與命中標示接到 <see cref="SuggestionList"/>。
/// </summary>
/// <remarks>
/// 沒有這個匯出，平台會用自己的比對器，詞首感知排名就會失效——
/// 輸入 <c>libr</c> 時 <c>Lib_Reader</c> 又會掉到含子字串的名稱後面。
/// 規則全在 Core；這裡只把平台的項目、按鈕狀態與命中區段換進換出。
///
/// 同時實作新舊兩版介面：平台優先呼叫
/// <see cref="IAsyncCompletionItemManager2"/> 的清單版本以避免多一次陣列複製，
/// 舊版仍必須存在才能完成 MEF 契約。
/// </remarks>
internal sealed class SqlAsyncCompletionItemManager : IAsyncCompletionItemManager, IAsyncCompletionItemManager2
{
    private static readonly Func<CompletionItem, string> DisplayTextOf = static item => item.DisplayText;

    private static readonly Func<CompletionItem, SqlSuggestion?> SuggestionOf = static item =>
        item.Properties.TryGetProperty<SqlSuggestion>(SqlAsyncCompletionSource.SuggestionKey, out var suggestion)
            ? suggestion
            : null;

    /// <summary>還沒輸入任何字元時的顯示順序，見 <see cref="SuggestionList.Sort"/>；只在 session 開始時做一次。</summary>
    public Task<CompletionList<CompletionItem>> SortCompletionItemListAsync(
        IAsyncCompletionSession session,
        AsyncCompletionSessionInitialDataSnapshot data,
        CancellationToken token)
    {
        // 排不成就照交進來的順序給回去：少了偏好排序的清單仍然可用，
        // 整條建議管線炸掉不是。與 UpdateCompletionListAsync 走同一族：
        // 這兩個方法都由平台的同一個非同步工作呼叫，它靠回傳的 Task 是不是
        // 取消狀態判斷這一輪作廢。
        //
        // CompletionList<T> 沒有公開建構式，重排過的清單只能由 session 生出來。
        // 排序與篩選都在按鍵路徑上，因此是 Typing／Debug：想知道清單為什麼慢的人
        // 才需要看到它們，平常一列都不該冒出來。
        using var notification = NotificationCenter.Default.Begin(NotificationCatalog.SortingSuggestions,
            NotificationKind.Completion, NotificationOrigin.Typing, NotificationLevel.Debug);
        return Task.FromResult(
            SqlAssistPlatformGuard.RunPropagatingCancellation(
                "建議清單排序",
                () => session.CreateCompletionList(SuggestionList.Sort(data.InitialItemList, SuggestionOf)),
                fallback: () => data.InitialItemList));
    }

    public Task<ImmutableArray<CompletionItem>> SortCompletionListAsync(
        IAsyncCompletionSession session,
        AsyncCompletionSessionInitialDataSnapshot data,
        CancellationToken token)
    {
        using var notification = NotificationCenter.Default.Begin(NotificationCatalog.SortingSuggestions,
            NotificationKind.Completion, NotificationOrigin.Typing, NotificationLevel.Debug);
        return Task.FromResult(
            SqlAssistPlatformGuard.RunPropagatingCancellation(
                "建議清單排序",
                () => SuggestionList.Sort(data.InitialItemList, SuggestionOf).ToImmutableArray(),
                fallback: () => data.InitialItemList.ToImmutableArray()));
    }

    public Task<FilteredCompletionModel?> UpdateCompletionListAsync(
        IAsyncCompletionSession session,
        AsyncCompletionSessionDataSnapshot data,
        CancellationToken token)
    {
        using var notification = NotificationCenter.Default.Begin(NotificationCatalog.FilteringSuggestions,
            NotificationKind.Completion, NotificationOrigin.Typing, NotificationLevel.Debug);
        // 這裡丟出例外會讓整個 session 掛掉；退回不篩選的完整清單。
        return Task.FromResult(
            SqlAssistPlatformGuard.RunPropagatingCancellation(
                "建議清單篩選",
                () => Filter(session, data, token),
                fallback: () => Unfiltered(data)));
    }

    /// <remarks>
    /// 回傳 null 時平台會關閉 session，只在一項都沒有時這樣做（見 <see cref="SuggestionListView{TItem}.IsEmpty"/>）。
    /// 選取一律交第 0 項、不帶選取提示（NoChange）：軟選只在來源建清單時決定一次，
    /// 使用者按 ↓ 轉成硬選之後就一直是硬選。
    /// </remarks>
    private static FilteredCompletionModel? Filter(
        IAsyncCompletionSession session,
        AsyncCompletionSessionDataSnapshot data,
        CancellationToken token)
    {
        var filterBar = session.Properties.TryGetProperty<SqlCompletionFilterBar>(
            SqlAsyncCompletionSource.FilterBarKey,
            out var bar)
            ? bar
            : null;

        var view = SuggestionList.Update(
            data.InitialSortedItemList,
            DisplayTextOf,
            SuggestionOf,
            GetTypedText(session, data),
            filterBar?.Selected(data.SelectedFilters) ?? SuggestionCategorySet.Empty,
            token);

        if (view.IsEmpty)
        {
            return null;
        }

        var items = view.Items;
        var builder = ImmutableArray.CreateBuilder<CompletionItemWithHighlight>(items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            var entry = items[index];
            builder.Add(entry.Spans.Count == 0
                ? new CompletionItemWithHighlight(entry.Item)
                : new CompletionItemWithHighlight(entry.Item, ToSpans(entry.Spans)));
        }

        // 平台只負責畫按鈕與記住按下的狀態；過濾是上面做的。沒有篩選列的清單（只有一類、
        // 或設定關掉）照平台交來的狀態原樣交回，而那份是空的。
        var filters = filterBar is null
            ? data.SelectedFilters
            : filterBar.States(view.Applied, view.Matched);

        return new FilteredCompletionModel(builder.MoveToImmutable(), 0, filters);
    }

    /// <summary>篩選失敗時的替代值：整份清單原樣交出，按鈕狀態原封不動。</summary>
    private static FilteredCompletionModel Unfiltered(AsyncCompletionSessionDataSnapshot data)
    {
        var builder = ImmutableArray.CreateBuilder<CompletionItemWithHighlight>(data.InitialSortedItemList.Count);

        foreach (var item in data.InitialSortedItemList)
        {
            builder.Add(new CompletionItemWithHighlight(item));
        }

        return new FilteredCompletionModel(builder.MoveToImmutable(), 0, data.SelectedFilters);
    }

    /// <summary>
    /// 使用者在建議範圍內已經輸入的文字；片段欄位的樣板預設值不算，見 <see cref="SuggestionList.TypedText"/>。
    /// </summary>
    private static string GetTypedText(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data)
    {
        var span = session.ApplicableToSpan;

        if (span is null)
        {
            return string.Empty;
        }

        var fieldDefault = session.Properties.TryGetProperty<string>(
            SqlAsyncCompletionSource.FieldDefaultKey,
            out var value)
            ? value
            : null;

        return SuggestionList.TypedText(span.GetText(data.Snapshot), fieldDefault);
    }

    private static ImmutableArray<Span> ToSpans(IReadOnlyList<MatchSpan> spans)
    {
        var builder = ImmutableArray.CreateBuilder<Span>(spans.Count);

        for (var index = 0; index < spans.Count; index++)
        {
            builder.Add(new Span(spans[index].Start, spans[index].Length));
        }

        return builder.MoveToImmutable();
    }
}
