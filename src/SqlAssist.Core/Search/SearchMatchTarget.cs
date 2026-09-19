using System;

namespace SqlAssist.Core.Search;

/// <summary>
/// 命中打在這個東西的哪一個部位上。
/// </summary>
/// <remarks>
/// 這是與<b>物件種類</b>（<see cref="SearchCategory"/>）互相獨立的第二條軸：一張資料表可以
/// 同時被名稱、資料行與定義本文命中，而那三件事講的是同一張表。兩條軸混在一起的症狀是
/// 「資料行」變成一種物件種類——使用者勾「只看資料表」時，資料行命中會整組消失，
/// 而他要找的正是某一張資料表上的那一行。
///
/// 值只增不改：<see cref="SearchQuery.Targets"/> 的旗標由這些值推出來，改一個等於改掉
/// provider 手上那份「這一輪要掃哪幾段」。分組顯示的先後<b>不</b>由值決定，
/// 見 <see cref="SearchMatchTargets.GroupOrder"/>。
/// </remarks>
public enum SearchMatchTarget
{
    /// <summary>名稱命中（物件名、收藏標題）。</summary>
    Name = 0,

    /// <summary>定義本文或內容命中（模組定義、SQL 全文、片段內容）。</summary>
    Text = 1,

    /// <summary>資料行名稱命中；指向的仍然是擁有它的那個物件。</summary>
    Column = 2
}

/// <summary>
/// 一輪搜尋要掃哪幾個部位；provider 據此跳過不必要的掃描。
/// </summary>
/// <remarks>
/// 做成旗標而不是三個布林，是因為它會一路傳到 provider 的最內層迴圈，而那裡要問的是
/// 「這一段要不要付」。<b>少掉 <see cref="Text"/> 的那一輪必須真的不去撈定義本文</b>——
/// 掃回來再丟掉的話，第一次搜尋最貴的那一段一毫秒都沒有省到，而使用者以為自己關掉了它。
/// </remarks>
[Flags]
public enum SearchTargets
{
    None = 0,

    Name = 1,

    Text = 2,

    Column = 4,

    All = Name | Text | Column
}

/// <summary>
/// <see cref="SearchMatchTarget"/> 的共用述詞；命名沿用 <c>SqlObjectKind</c>／<c>SqlObjectKinds</c> 那一對。
/// </summary>
public static class SearchMatchTargets
{
    /// <summary>這個部位對應的旗標。</summary>
    public static SearchTargets ToFlag(this SearchMatchTarget target)
    {
        return target switch
        {
            SearchMatchTarget.Name => SearchTargets.Name,
            SearchMatchTarget.Text => SearchTargets.Text,
            SearchMatchTarget.Column => SearchTargets.Column,
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
    }

    /// <summary>
    /// 清單上的分組先後；越小越前面。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不</b>拿列舉值當順序。<see cref="SearchMatchTarget.Column"/> 是後來從物件種類那條軸
    /// 搬過來的，接在後面才不會改掉既有的值；但它在畫面上屬於名稱那一族——使用者打
    /// <c>CopyNo</c> 要的是「叫這個名字的資料行在哪幾張表」，而定義本文裡剛好也提到這幾個字的
    /// 那一堆模組排在它後面。照列舉值排的話，本文命中會插在名稱與資料行中間。
    ///
    /// 分數不跨組比較：名稱與資料行那邊是
    /// <see cref="SqlAssist.Core.Matching.FuzzyMatcher"/> 的詞首加成，本文那邊是出現次數，
    /// 兩個尺度混在一起排的結果是使用者打表名，先看到的卻是一堆註解裡也有那幾個字的定義本文。
    /// </remarks>
    public static int GroupOrder(this SearchMatchTarget target)
    {
        return target switch
        {
            SearchMatchTarget.Name => 0,
            SearchMatchTarget.Column => 1,
            SearchMatchTarget.Text => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
    }
}
