using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SqlAssist.Core.Matching;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 建議清單在一個 session 裡的順序與可見度：開場排一次，之後每一鍵依輸入篩選、排名。
/// </summary>
/// <remarks>
/// 平台把排序與篩選交給擴充自己的 item manager；那一層只轉接平台型別，規則全在這裡，
/// 測試守的就是產品走的那一條。候選能不能出現在這個位置是另一件事，建立清單時由
/// <see cref="SuggestionContextFilter.Filter"/> 做完，這裡不再過問。
///
/// 項目型別是泛型：呼叫端交出自己的項目與兩個讀取器，這裡不必為每一鍵另建一份模型陣列。
/// 同一個 session 裡別的來源的項目沒有建議項，照同一套尺度排名，但不參與分類篩選與
/// 無前綴隱藏——它們不該因為使用者按了我們的按鈕而無聲消失。
/// </remarks>
public static class SuggestionList
{
    /// <summary>
    /// 有前綴時最多交出幾筆。
    /// </summary>
    /// <remarks>
    /// 這是效能保險，不是偏好：在有數千個物件的資料庫裡輸入一個字元，
    /// 沒有上限就要為每一筆配置命中區段並排序。使用者感覺不到差別——
    /// 清單本來就要捲動，而且再多打一個字，排名就整個重算了。
    /// </remarks>
    public const int MaximumItems = 300;

    /// <summary>
    /// 還沒輸入任何字元時的顯示順序；session 開始時排一次。
    /// </summary>
    /// <remarks>
    /// 交進來的順序是候選清單的串接順序——關鍵字與程式碼片段、敘述範圍欄位、
    /// 資料庫物件——照那個順序顯示，按 Ctrl+Space 會先看到一整排關鍵字，
    /// 敘述裡的欄位要捲很久才看得到。
    ///
    /// 這裡依與輸入無關的那一段分數（類別偏好，其次最近用過）穩定排序，因此同一類別內
    /// 仍是原本的順序（欄位＝資料表定義順序，物件與關鍵字＝名稱順序）。之後每一鍵的
    /// <see cref="Update"/> 都拿這份排好的清單當輸入。
    /// </remarks>
    public static List<TItem> Sort<TItem>(IReadOnlyList<TItem> items, Func<TItem, SqlSuggestion?> suggestionOf)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        if (suggestionOf is null)
        {
            throw new ArgumentNullException(nameof(suggestionOf));
        }

        // List.Sort 不穩定，會把同分項目的原順序打散；這裡要的正是「同分維持原序」。
        return items
            .OrderByDescending(item => SuggestionScore.ComposeStanding(suggestionOf(item)))
            .ToList();
    }

    /// <summary>
    /// 使用者在建議範圍內真正打的字。
    /// </summary>
    /// <remarks>
    /// 原生 Snippet 欄位剛進去時，整格是<b>樣板填的預設值</b>而不是使用者打的字。
    /// 拿它當篩選前綴會把清單濾光——<c>dbo.TargetTable</c> 比不中任何一個資料表
    /// 名稱，一個都沒中時平台會把剛開的 session 直接關掉，看起來就是
    /// 「Tab 進去沒有清單，打了字才有」。
    ///
    /// 比對的是<b>當下</b>的文字，不是一個記在 session 上的旗標：使用者一打字，
    /// 格子內容就不再等於預設值，這裡自然恢復正常比對，不必有人去清狀態。
    /// </remarks>
    /// <param name="applicableText">建議範圍內目前的文字。</param>
    /// <param name="fieldDefault">這一格的樣板預設值；不在片段欄位裡時為 null。</param>
    public static string TypedText(string applicableText, string? fieldDefault)
    {
        if (applicableText is null)
        {
            throw new ArgumentNullException(nameof(applicableText));
        }

        return fieldDefault is not null && string.Equals(applicableText, fieldDefault, StringComparison.Ordinal)
            ? string.Empty
            : applicableText;
    }

    /// <summary>
    /// 這一鍵之後清單上看得到的項目、順序與分類鈕的狀態。
    /// </summary>
    /// <remarks>
    /// 分類篩選照 <see cref="SuggestionCategoryFilter"/> 的規則：先不看分類把每一項比對完，
    /// 哪幾類還有命中要看全部的命中才知道；按著的分類與命中取交集才是這一輪套用的。
    /// 套用的分類一定有命中，所以分類篩選不會把有命中的清單篩空。
    ///
    /// 沒有前綴時不評分，順序原樣沿用 <see cref="Sort"/> 排好的那一份：比對品質此時全是零，
    /// 再評一次分只剩名稱長度懲罰會說話，同類別內的欄位就不再是資料表的定義順序。
    /// 危險片段在這時隱藏，除非使用者按了分類鈕；片段在開場排序裡已經降分。
    ///
    /// 空前綴什麼都比得中，所以每一類都算有命中。危險片段隱藏是呈現規則，不是比對結果：
    /// 算成沒命中的話，只剩危險片段的那一類會變灰，主動按那顆也叫不出來。
    /// </remarks>
    /// <param name="sortedItems"><see cref="Sort"/> 排好的清單。</param>
    /// <param name="displayTextOf">清單上顯示、也是比對的文字。</param>
    /// <param name="suggestionOf">項目的建議項；別的來源的項目回傳 null。</param>
    /// <param name="typedText">使用者打的字（見 <see cref="TypedText"/>）。</param>
    /// <param name="selected">使用者目前按著的分類。</param>
    /// <param name="token">平台作廢這一輪時取消。</param>
    public static SuggestionListView<TItem> Update<TItem>(
        IReadOnlyList<TItem> sortedItems,
        Func<TItem, string> displayTextOf,
        Func<TItem, SqlSuggestion?> suggestionOf,
        string typedText,
        SuggestionCategorySet selected,
        CancellationToken token = default)
    {
        if (sortedItems is null)
        {
            throw new ArgumentNullException(nameof(sortedItems));
        }

        if (displayTextOf is null)
        {
            throw new ArgumentNullException(nameof(displayTextOf));
        }

        if (suggestionOf is null)
        {
            throw new ArgumentNullException(nameof(suggestionOf));
        }

        var pattern = FuzzyMatcher.NormalizePattern(typedText ?? throw new ArgumentNullException(nameof(typedText)));
        var hasPrefix = pattern.Length > 0;
        var candidates = new List<Candidate>(sortedItems.Count);
        var matched = SuggestionCategorySet.Empty;

        // 套用的分類是按著的與命中的交集，一般得等全部比對完才知道；什麼都沒按時交集一定是空的，
        // 每一項當場就能決定去留，不必再走第二趟。
        var decided = selected.IsEmpty;

        for (var index = 0; index < sortedItems.Count; index++)
        {
            token.ThrowIfCancellationRequested();

            var item = sortedItems[index];
            var displayText = string.Empty;
            var match = FuzzyMatchResult.NoMatch;

            // 先比對、命中了才讀建議項：多數項目在這一步就被淘汰。
            if (hasPrefix)
            {
                displayText = displayTextOf(item);
                match = FuzzyMatcher.MatchNormalized(pattern, displayText);

                if (!match.IsMatch)
                {
                    continue;
                }
            }

            var suggestion = suggestionOf(item);
            var category = suggestion is null ? null : SuggestionCategories.Of(suggestion.Kind);

            if (category is { } known)
            {
                matched = matched.With(known);
            }

            var candidate = new Candidate(
                index,
                hasPrefix ? SuggestionScore.Compose(displayText, suggestion, match, pattern) : 0,
                match.Spans,
                category,
                suggestion?.IsDestructive == true);

            if (!decided || IsShown(candidate, SuggestionCategorySet.Empty, hasPrefix))
            {
                candidates.Add(candidate);
            }
        }

        var applied = SuggestionCategoryFilter.Apply(selected, matched);

        if (!decided)
        {
            // 原地壓實：不另配置一份篩過的。
            var kept = 0;

            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];

                if (IsShown(candidate, applied, hasPrefix))
                {
                    candidates[kept++] = candidate;
                }
            }

            candidates.RemoveRange(kept, candidates.Count - kept);
        }

        if (hasPrefix)
        {
            candidates.Sort(ByScore.Instance);

            if (candidates.Count > MaximumItems)
            {
                candidates.RemoveRange(MaximumItems, candidates.Count - MaximumItems);
            }
        }

        return new SuggestionListView<TItem>(sortedItems, candidates, matched, applied);
    }

    /// <remarks>
    /// 沒有分類的項目一律留下：不參與分類篩選的種類（見 <see cref="SuggestionCategories.Of"/>），
    /// 以及同一個 session 裡別的來源的項目。
    /// </remarks>
    private static bool IsShown(in Candidate candidate, SuggestionCategorySet applied, bool hasPrefix)
    {
        if (candidate.Category is { } category && !SuggestionCategoryFilter.Includes(applied, category))
        {
            return false;
        }

        // Ctrl+Space 的空前綴首頁不主動列危險片段；主動按了分類就是要看，不再隱藏。
        return hasPrefix || !candidate.IsDestructive || !applied.IsEmpty;
    }

    /// <summary>
    /// 一項命中在排序與篩選期間的樣子。
    /// </summary>
    /// <remarks>
    /// 每一鍵都要為每一筆命中配置一格，這一格越小越好：不帶項目本身，只記它在交進來那份清單裡的
    /// 位置；分類與危險旗標擠進位置的高位，整格與平台一筆清單項目一樣是 16 位元組。
    /// 位置只用到低 24 位元，一份清單不會有一千六百萬項。
    /// </remarks>
    internal readonly struct Candidate
    {
        private const int OrderBits = 24;
        private const int OrderMask = (1 << OrderBits) - 1;
        private const int CategoryMask = 0x1F;
        private const int DestructiveFlag = 1 << (OrderBits + 5);

        private readonly int _packed;

        public Candidate(int order, int score, IReadOnlyList<MatchSpan> spans, SuggestionCategory? category, bool isDestructive)
        {
            if ((uint)order > OrderMask)
            {
                throw new ArgumentOutOfRangeException(nameof(order));
            }

            // 分類存成加一，零留給「沒有分類」。
            var categoryBits = category is { } known ? (int)known + 1 : 0;
            _packed = order | (categoryBits << OrderBits) | (isDestructive ? DestructiveFlag : 0);
            Score = score;
            Spans = spans;
        }

        /// <summary>在交進來那份清單裡的位置；同分時靠它維持原序。</summary>
        public int Order => _packed & OrderMask;

        public int Score { get; }

        public IReadOnlyList<MatchSpan> Spans { get; }

        public SuggestionCategory? Category
        {
            get
            {
                var categoryBits = (_packed >> OrderBits) & CategoryMask;
                return categoryBits == 0 ? null : (SuggestionCategory)(categoryBits - 1);
            }
        }

        public bool IsDestructive => (_packed & DestructiveFlag) != 0;
    }

    /// <summary>
    /// 分數由高到低；同分時保留交進來的順序。
    /// </summary>
    /// <remarks>
    /// <see cref="List{T}.Sort(IComparer{T})"/> 不穩定，所以同分時比原本的位置，排出來與穩定排序相同。
    /// 不改成字母序：交進來的清單已經是排好的——欄位是資料表定義順序，物件與關鍵字是名稱順序，
    /// 字母序會把欄位的定義順序打散，而那才是使用者對一張表的心智模型。
    /// </remarks>
    private sealed class ByScore : IComparer<Candidate>
    {
        public static readonly ByScore Instance = new();

        public int Compare(Candidate x, Candidate y)
        {
            var byScore = y.Score.CompareTo(x.Score);
            return byScore != 0 ? byScore : x.Order.CompareTo(y.Order);
        }
    }
}
