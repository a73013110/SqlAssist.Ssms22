using System;
using System.Data.Common;

namespace SqlAssist.Metadata.Caching;

/// <summary>
/// 中繼資料查詢失敗時，把資料庫說的那句話送出去的唯一出口。
/// </summary>
/// <remarks>
/// <see cref="SqlMetadataCatalog"/> 把 <see cref="DbException"/> 降級成「這一輪沒有
/// 資料」，理由是紀錄檔的訊噪比：冒到平台邊界會讓連線斷掉時每按一次鍵留下一份
/// 完整堆疊。但降級到<b>一個字都不留</b>是另一回事——查詢寫錯（欄位名打錯、
/// 這一版伺服器沒有那個目錄檢視欄位）與連線中斷在畫面上長得一模一樣，
/// 而唯一分得出來的資訊正是被吃掉的那句 <c>Invalid column name '…'</c>。
///
/// 因此這裡只送訊息不送堆疊，並且由接線端決定要不要寫：Ssms22 接到
/// 「詳細記錄」那一段，平常一個位元組都不寫，出問題時打開就看得到是哪一條查詢、
/// 伺服器說了什麼。
///
/// 這一層不能參照 Ssms22（護欄：Metadata 只依賴 <c>System.Data</c>），
/// 所以用委派而不是直接呼叫紀錄器。沒有接線時整條是 no-op。
/// </remarks>
public static class SqlMetadataFailure
{
    /// <summary>接線端；由 Ssms22 在套件初始化時指定，測試裡也可以換掉。</summary>
    public static Action<string, DbException>? Reporter { get; set; }

    /// <param name="operation">哪一條查詢；訊息裡唯一能指出「改哪裡」的東西。</param>
    internal static void Report(string operation, DbException exception)
    {
        // 回報本身失敗不能讓查詢的降級路徑再丟一次例外——那會冒出 SqlMetadataCatalog，
        // 正是這一族要避免的事。
        try
        {
            Reporter?.Invoke(operation, exception);
        }
        catch (Exception)
        {
            // 診斷不可影響功能。
        }
    }
}
