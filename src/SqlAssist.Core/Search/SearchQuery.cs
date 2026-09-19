using System;
using System.Collections.Generic;
using SqlAssist.Core.Matching;

namespace SqlAssist.Core.Search;

/// <summary>
/// 一輪搜尋的完整輸入；不可變，可以同時交給多個 provider。
/// </summary>
/// <remarks>
/// 不可變是因為同一個實例會被好幾個 provider 在不同執行緒上讀：可變的話，
/// 使用者多打一個字就足以讓其中一個 provider 用新樣式、另一個用舊樣式比對，
/// 而兩邊的結果會被排進同一份清單。每一次輸入改變都建一個新的實例，
/// 並把 <see cref="Generation"/> 加一。
/// </remarks>
public sealed class SearchQuery
{
    private static readonly HashSet<string> NoCategories = new(StringComparer.Ordinal);

    private readonly HashSet<string> _categories;

    /// <param name="generation">
    /// 這是第幾輪輸入，只增不減。聚合器用它作廢落後的結果，詳見
    /// <see cref="SearchAggregator.SearchAsync"/>。
    /// </param>
    /// <param name="categories">
    /// 要保留的 <see cref="SearchCategory.Id"/>；null 或空表示不過濾。
    /// </param>
    /// <param name="targets">
    /// 這一輪要掃哪幾個部位。provider 必須真的據此跳過掃描，不是掃回來再丟。
    /// </param>
    public SearchQuery(
        string text,
        long generation = 0,
        SearchOptions options = SearchOptions.None,
        IEnumerable<string>? categories = null,
        SearchScope? scope = null,
        SearchTargets targets = SearchTargets.All)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));

        // 一個部位都不掃的查詢一定是呼叫端算錯了：它不會找到任何東西，而畫面上與
        // 「這個字串不存在」一模一樣。認不得的位元同理——多出來的那一位沒有人會去掃。
        if (targets == SearchTargets.None || (targets & ~SearchTargets.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targets));
        }

        Text = text;
        Generation = generation;
        Options = options;
        Scope = scope ?? SearchScope.All;
        Targets = targets;
        NormalizedPattern = FuzzyMatcher.NormalizePattern(text);
        _categories = Copy(categories);
    }

    /// <summary>使用者打進去的原文，原樣保留（含大小寫與前後空白）。</summary>
    public string Text { get; }

    /// <summary>
    /// <see cref="FuzzyMatcher.NormalizePattern"/> 的結果，建立查詢時算一次。
    /// </summary>
    /// <remarks>
    /// 快取在這裡而不是讓 provider 各自呼叫：目錄物件 provider 一輪要比對上萬個候選，
    /// 每個候選前都正規化一次樣式，等於在最熱的迴圈裡多配置一個字串。
    /// provider 應該用 <see cref="FuzzyMatcher.MatchNormalized"/> 搭配這個欄位。
    /// </remarks>
    public string NormalizedPattern { get; }

    /// <summary>第幾輪輸入；只增不減。</summary>
    public long Generation { get; }

    public SearchOptions Options { get; }

    public SearchScope Scope { get; }

    /// <summary>
    /// 這一輪要掃哪幾個部位。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="Categories"/> 是兩條獨立的軸：前者說「掃它的哪裡」，後者說「留哪一種東西」。
    /// 目錄物件 provider 的第一次搜尋最貴的一段正是定義本文——不含
    /// <see cref="SearchTargets.Text"/> 的那一輪連撈都不撈，也不佔記憶體。
    /// </remarks>
    public SearchTargets Targets { get; }

    /// <summary>要保留的分類 Id；空表示不過濾。</summary>
    public IReadOnlyCollection<string> Categories => _categories;

    public bool HasCategoryFilter => _categories.Count > 0;

    /// <summary>沒有輸入的一輪；provider 可以據此列出預設清單而不是一筆都不回。</summary>
    public bool IsEmpty => Text.Length == 0;

    /// <summary>
    /// 這個分類這一輪要不要留。
    /// </summary>
    /// <remarks>
    /// ordinal 比對，因為 <see cref="SearchCategory.Id"/> 是程式給的穩定識別字而不是人打的字；
    /// 放寬成不分大小寫，症狀是兩個只差大小寫的 Id 會被當成同一個分類，
    /// 而它們是不同 provider 各自宣告的。
    /// </remarks>
    public bool MatchesCategory(string categoryId)
    {
        if (categoryId is null) throw new ArgumentNullException(nameof(categoryId));
        return _categories.Count == 0 || _categories.Contains(categoryId);
    }

    /// <summary>這個部位這一輪要不要掃。</summary>
    public bool IncludesTarget(SearchMatchTarget target) => (Targets & target.ToFlag()) != 0;

    private static HashSet<string> Copy(IEnumerable<string>? categories)
    {
        if (categories is null) return NoCategories;

        var copy = new HashSet<string>(StringComparer.Ordinal);

        foreach (var category in categories)
        {
            if (string.IsNullOrEmpty(category)) throw new ArgumentException("分類 Id 不可為空。", nameof(categories));
            copy.Add(category);
        }

        return copy.Count == 0 ? NoCategories : copy;
    }
}
