using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Matching;
using SqlAssist.Ssms22;

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
    /// 篩選後最多交出幾筆。
    /// </summary>
    /// <remarks>
    /// 這是效能保險，不是偏好：在有數千個物件的資料庫裡輸入一個字元，
    /// 沒有上限就要為每一筆配置命中區段並排序。使用者感覺不到差別——
    /// 清單本來就要捲動，而且再多打一個字，排名就整個重算了。
    /// </remarks>
    private const int MaximumItems = 300;

    /// <summary>
    /// 還沒輸入任何字元時的顯示順序。
    /// </summary>
    /// <remarks>
    /// 交進來的順序是候選清單的串接順序——關鍵字與程式碼片段、敘述範圍欄位、
    /// 資料庫物件——照那個順序顯示，按 Ctrl+Space 會先看到一整排關鍵字，
    /// 敘述裡的欄位要捲很久才看得到。
    ///
    /// 這裡依 <see cref="SuggestionMatcher.ComposeStandingScore"/> 排一次：
    /// 最近用過的在最前面，接著才是類別偏好。排序是穩定的，因此同一類別內
    /// 仍是原本的順序（欄位＝資料表定義順序，物件與關鍵字＝名稱順序）。
    ///
    /// 只在 session 開始時做一次，之後每一次按鍵拿到的都是這份排好的清單。
    /// </remarks>
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
        return Task.FromResult(
            SqlAssistPlatformGuard.RunPropagatingCancellation(
                "建議清單排序",
                () => session.CreateCompletionList(Sort(data.InitialItemList)),
                fallback: () => data.InitialItemList));
    }

    public Task<ImmutableArray<CompletionItem>> SortCompletionListAsync(
        IAsyncCompletionSession session,
        AsyncCompletionSessionInitialDataSnapshot data,
        CancellationToken token)
    {
        return Task.FromResult(
            SqlAssistPlatformGuard.RunPropagatingCancellation(
                "建議清單排序",
                () => Sort(data.InitialItemList).ToImmutableArray(),
                fallback: () => data.InitialItemList.ToImmutableArray()));
    }

    /// <summary>依與輸入無關的那一段分數穩定排序。</summary>
    private static List<CompletionItem> Sort(CompletionList<CompletionItem> items)
    {
        var sorted = new List<CompletionItem>(items.Count);

        foreach (var item in items)
        {
            sorted.Add(item);
        }

        // List.Sort 不穩定，會把同分項目的原順序打散；這裡要的正是「同分維持原序」。
        return sorted
            .OrderByDescending(StandingScore)
            .ToList();
    }

    /// <summary>項目在沒有輸入前綴時的分數。</summary>
    private static int StandingScore(CompletionItem item)
    {
        return item.Properties.TryGetProperty<SqlSuggestion>(
            SqlAsyncCompletionSource.SuggestionKey,
            out var suggestion) && suggestion is not null
            ? SuggestionMatcher.ComposeStandingScore(suggestion)
            : 0;
    }

    public Task<FilteredCompletionModel?> UpdateCompletionListAsync(
        IAsyncCompletionSession session,
        AsyncCompletionSessionDataSnapshot data,
        CancellationToken token)
    {
        // 這裡丟出例外會讓整個 session 掛掉；退回不篩選的完整清單。
        return Task.FromResult(
            SqlAssistPlatformGuard.RunPropagatingCancellation(
                "建議清單篩選",
                () => Filter(session, data, token),
                fallback: () => Passthrough(data, GetSelectedFilters(data))));
    }

    private static FilteredCompletionModel? Filter(
        IAsyncCompletionSession session,
        AsyncCompletionSessionDataSnapshot data,
        CancellationToken token)
    {
        var items = data.InitialSortedItemList;
        var selected = GetSelectedFilters(data);
        var pattern = FuzzyMatcher.NormalizePattern(GetTypedText(session, data));

        if (pattern.Length == 0)
        {
            return Passthrough(data, selected);
        }

        var scored = new List<ScoredItem>(items.Count);

        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();

            if (!IsIncluded(item, selected))
            {
                continue;
            }

            var match = FuzzyMatcher.MatchNormalized(pattern, item.DisplayText);

            if (!match.IsMatch)
            {
                continue;
            }

            scored.Add(new ScoredItem(item, ComposeScore(item, match, pattern), match.Spans));
        }

        if (scored.Count == 0)
        {
            // 一個都沒中時回傳 null，平台會關閉 session，
            // 而不是留一份空清單擋在游標旁邊。
            //
            // 但被分類篩選器篩空時不能關：篩選列會跟著消失，
            // 使用者連取消剛才按下的那顆都做不到。留一份空清單等他再按一次。
            return selected.Count == 0
                ? null
                : new FilteredCompletionModel(
                    ImmutableArray<CompletionItemWithHighlight>.Empty,
                    0,
                    SqlCompletionFilters.Sort(data.SelectedFilters));
        }

        // 同分時保留原順序（排序是穩定的），不改成字母序：交進來的清單已經是
        // 排好的——欄位是資料表定義順序，物件與關鍵字是名稱順序。
        var filtered = scored
            .OrderByDescending(entry => entry.Score)
            .Take(MaximumItems)
            .Select(entry => new CompletionItemWithHighlight(entry.Item, ToSpans(entry.Spans)))
            .ToImmutableArray();

        return new FilteredCompletionModel(filtered, 0, SqlCompletionFilters.Sort(data.SelectedFilters));
    }

    /// <summary>
    /// 還沒輸入任何字元時的清單：只套用分類篩選，順序原樣保留。
    /// </summary>
    /// <remarks>
    /// 不走上面的評分路徑，是因為空前綴時所有分數相同，一排序就會被
    /// <c>ThenBy(DisplayText)</c> 重排，敘述範圍內的欄位優先這件事就沒了。
    /// </remarks>
    private static FilteredCompletionModel Passthrough(
        AsyncCompletionSessionDataSnapshot data,
        List<CompletionFilter> selected)
    {
        var builder = ImmutableArray.CreateBuilder<CompletionItemWithHighlight>(data.InitialSortedItemList.Count);

        foreach (var item in data.InitialSortedItemList)
        {
            if (IsIncluded(item, selected) && !IsDestructiveWithoutPrefix(item, selected))
            {
                builder.Add(new CompletionItemWithHighlight(item));
            }
        }

        return new FilteredCompletionModel(
            builder.ToImmutable(),
            0,
            SqlCompletionFilters.Sort(data.SelectedFilters));
    }

    private static bool IsDestructiveWithoutPrefix(
        CompletionItem item,
        IReadOnlyCollection<CompletionFilter> selected)
    {
        // 使用者主動按了分類篩選鈕時照樣列出；只有 Ctrl+Space 的「全部」首頁隱藏。
        return item.Properties.TryGetProperty<SqlSuggestion>(
                   SqlAsyncCompletionSource.SuggestionKey,
                   out var suggestion) &&
               suggestion is not null &&
               !SuggestionMatcher.IsVisibleWithoutPrefix(
                   suggestion,
                   categorySelected: selected.Count > 0);
    }

    /// <summary>使用者按下的分類篩選鈕。</summary>
    /// <remarks>
    /// 平台只負責畫這排按鈕與記住按下的狀態；過濾要自己做。
    /// 清單由這個 item manager 產出，不讀這份狀態的話，按鈕會按得下去卻沒有作用。
    /// </remarks>
    private static List<CompletionFilter> GetSelectedFilters(AsyncCompletionSessionDataSnapshot data)
    {
        var selected = new List<CompletionFilter>(data.SelectedFilters.Length);

        foreach (var state in data.SelectedFilters)
        {
            if (state.IsSelected)
            {
                selected.Add(state.Filter);
            }
        }

        return selected;
    }

    /// <summary>
    /// 這一項通過分類篩選了嗎。
    /// </summary>
    /// <remarks>
    /// 沒按任何一顆＝全部，因此不必另外做一顆「全部」。
    ///
    /// 沒有分類的項目一律列出。這是防呆而不是設計：
    /// <see cref="SqlCompletionFilters.For"/> 會把每一種建議都歸到一顆篩選鈕上
    /// （歸不了的收在「其他」），所以掛著篩選列時走不到這條。真的走到了——
    /// 例如日後多出一種沒對應到的項目——寧可讓它照樣出現，也不要無聲消失。
    /// </remarks>
    private static bool IsIncluded(CompletionItem item, List<CompletionFilter> selected)
    {
        if (selected.Count == 0 || item.Filters.IsDefaultOrEmpty)
        {
            return true;
        }

        foreach (var filter in item.Filters)
        {
            if (selected.Contains(filter))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 取得使用者在建議範圍內已經輸入的文字。
    /// </summary>
    /// <remarks>
    /// 原生 Snippet 欄位剛進去時，整格是<b>樣板填的預設值</b>而不是使用者打的字。
    /// 拿它當篩選前綴會把清單濾光——<c>dbo.TargetTable</c> 比不中任何一個資料表
    /// 名稱，而 <see cref="Filter"/> 一個都沒中就回 null，平台會把我們剛開的
    /// session 直接關掉，看起來就是「Tab 進去沒有清單，打了字才有」。
    ///
    /// 比對的是<b>當下</b>的文字，不是一個記在 session 上的旗標：使用者一打字，
    /// 格子內容就不再等於預設值，這裡自然恢復正常比對，不必有人去清狀態。
    /// </remarks>
    private static string GetTypedText(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data)
    {
        var span = session.ApplicableToSpan;

        if (span is null)
        {
            return string.Empty;
        }

        var text = span.GetText(data.Snapshot);

        return session.Properties.TryGetProperty<string>(
                   SqlAsyncCompletionSource.FieldDefaultKey,
                   out var fieldDefault) &&
               string.Equals(text, fieldDefault, StringComparison.Ordinal)
            ? string.Empty
            : text;
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
