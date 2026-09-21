using System;
using SqlAssist.Core.Matching;

namespace SqlAssist.Core.Search;

/// <summary>
/// 一輪搜尋的布林選項。
/// </summary>
/// <remarks>
/// 做成旗標而不是一個個屬性，是為了讓之後加選項不必動 <see cref="SearchQuery"/> 的建構子
/// 與每一個 provider 的簽章；v1 不做 regex 與 facet 語法，但它們加進來時就是多一個位元
/// （<c>UseRegex = 4</c>、<c>ParseFacets = 8</c>），呼叫端與既有 provider 都不必重編。
///
/// 兩個位元的語意對<b>每一個部位</b>都一樣：名稱、資料行與定義本文各自怎麼掃仍是 provider
/// 的事，但「大小寫要不要算」與「前後要不要是詞界」的答案三處必須相同。只套在本文那一段的
/// 那一版，症狀是使用者兩顆都開著，清單上仍然出現 <c>finish</c> 的字母散落在
/// <c>DF_Form_LeaveKind_isShow</c> 各處的那一筆——他關掉的東西一個都沒關掉。
///
/// 實際怎麼比在 <see cref="SearchIdentifierMatch"/>（名稱與資料行）與
/// <c>SqlCatalogBodySearch</c>（定義本文），兩邊都走 <see cref="ToProjectionMode"/> 換出來的
/// 同一份規則；聚合器只負責把選項原樣傳下去。
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

/// <summary><see cref="SearchOptions"/> 換算成比對那一層認得的規則。</summary>
public static class SearchOptionsExtensions
{
    /// <summary>
    /// 這一輪的字面比對要用哪一種規則。
    /// </summary>
    /// <remarks>
    /// 換算只有一份：名稱、資料行與定義本文各自再讀一次旗標的那一版，症狀是其中一處把
    /// 「沒勾大小寫」讀成 ordinal，而同一個字串在兩個部位上命中的筆數不一樣。
    /// </remarks>
    public static MatchProjectionMode ToProjectionMode(this SearchOptions options)
    {
        var mode = MatchProjectionMode.None;

        if ((options & SearchOptions.WholeWord) != 0) mode |= MatchProjectionMode.WholeWord;
        // 預設是不分大小寫，所以這一位是反過來的：沒勾才加 IgnoreCase。
        if ((options & SearchOptions.MatchCasing) == 0) mode |= MatchProjectionMode.IgnoreCase;

        return mode;
    }
}
