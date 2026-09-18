namespace SqlAssist.Core.Search;

/// <summary>
/// 命中是打在名稱上還是打在內容裡。
/// </summary>
/// <remarks>
/// 列舉值的大小就是分組順序，排名直接拿它比：<see cref="Name"/> 的整組排在
/// <see cref="Body"/> 的整組之前，分數再低也一樣。兩者不互相壓過去，是因為分數根本
/// 不是同一個尺度——名稱那邊是 <see cref="SqlAssist.Core.Matching.FuzzyMatcher"/>
/// 的詞首加成，本文那邊是出現次數與位置。混在一起排的結果是使用者打表名，
/// 先看到的卻是一堆註解裡剛好也有那幾個字的定義本文。
///
/// 新增值要接在後面，不要插進中間：值就是順序。
/// </remarks>
public enum SearchHitClass
{
    /// <summary>名稱命中（物件名、欄位名、收藏標題）。</summary>
    Name = 0,

    /// <summary>定義本文或內容命中（模組定義、SQL 全文、片段內容）。</summary>
    Body = 1
}
