namespace SqlAssist.Core.Parsing;

/// <summary>
/// 限定字的一格，由右往左數。
/// </summary>
/// <remarks>
/// 順序就是右對齊的順序，<see cref="SqlObjectPath.TryRealign"/> 直接拿它當位移量，
/// 因此不可以重新排列或插入新值。
///
/// 這個型別存在的理由是：只看文字分不出 <c>dbo.</c>、<c>LibArchive.</c> 與
/// <c>SQL209.</c>——三者都是一段限定字。分得出來要知道這台伺服器上有哪些資料庫、
/// 掛了哪些連結伺服器，那是中繼資料的事。中繼資料只回答「最左邊那一段是什麼」，
/// 段位怎麼跟著挪由 <see cref="SqlObjectPath"/> 算，兩邊各算一次的話，
/// 同一個名稱會在建議清單上認得、在插入文字那條路上少一段。
/// </remarks>
public enum SqlQualifierSlot
{
    /// <summary>結構描述或別名；右對齊之後最右邊永遠是這一格。</summary>
    Schema,

    /// <summary>資料庫。</summary>
    Database,

    /// <summary>伺服器（本機或連結伺服器）。</summary>
    Server
}
