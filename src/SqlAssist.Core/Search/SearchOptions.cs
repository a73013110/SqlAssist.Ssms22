using System;

namespace SqlAssist.Core.Search;

/// <summary>
/// 一輪搜尋的布林選項。
/// </summary>
/// <remarks>
/// 做成旗標而不是一個個屬性，是為了讓之後加選項不必動 <see cref="SearchQuery"/> 的建構子
/// 與每一個 provider 的簽章；v1 不做 regex 與 facet 語法，但它們加進來時就是多一個位元
/// （<c>UseRegex = 4</c>、<c>ParseFacets = 8</c>），呼叫端與既有 provider 都不必重編。
///
/// Core 不代為套用這些選項：<see cref="SqlAssist.Core.Matching.FuzzyMatcher"/> 本身不分大小寫，
/// 而「整個字」對識別字與對定義本文是兩件不同的事。由 provider 自己決定怎麼讀，
/// 聚合器只負責原樣傳下去。
/// </remarks>
[Flags]
public enum SearchOptions
{
    None = 0,

    /// <summary>區分大小寫。</summary>
    MatchCasing = 1,

    /// <summary>只取整個字的命中。</summary>
    WholeWord = 2
}
