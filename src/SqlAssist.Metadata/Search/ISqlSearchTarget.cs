namespace SqlAssist.Metadata.Search;

/// <summary>
/// 住在某一台 SQL Server 上的搜尋結果酬載。
/// </summary>
/// <remarks>
/// 接線層只憑這一個介面就知道一筆結果屬於哪一台，不必逐一認得每一種酬載：
/// 在物件總管上找哪一台、能不能沿用查詢視窗那條連線、目錄是不是同一台，
/// 三道判斷都只讀 <see cref="Origin"/>。新來源的酬載若是伺服器上的東西就實作它，
/// 那三道判斷自動套上，不必在每一條路徑各補一次。
/// </remarks>
public interface ISqlSearchTarget
{
    /// <summary>這一筆是在哪一台伺服器上搜到的。</summary>
    SqlSearchOrigin Origin { get; }
}
