using System;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>SQL Memory 清單的狀態表面要說什麼；與 SQL Search 共用同一份表面，只是決定的依據不同。</summary>
internal static class SqlMemorySurfaceState
{
    /// <summary>
    /// 載入、空與讀不到三選一。
    /// </summary>
    /// <param name="footer">這一輪的頁尾；空狀態的文案由它提供，說法只有一份。</param>
    /// <param name="loading">目前有一輪在讀。</param>
    /// <param name="rowCount">清單上已經有幾列。</param>
    /// <param name="failure">這一輪讀清單失敗的訊息；沒有失敗時空字串。</param>
    /// <remarks>
    /// 只在一列都沒有時遮住清單：續頁的進度與續頁的失敗都留在頁尾與狀態列，蓋住已經
    /// 讀得到的那幾十筆沒有道理。
    ///
    /// SQL Memory 讀的是本機檔案，沒有「權限不足」那一種——磁碟權限失敗與其他 I/O 失敗
    /// 在這裡分不開，一律走讀不到那一個出口。
    /// </remarks>
    public static SqlSurfaceState For(SqlMemoryFooter footer, bool loading, int rowCount, string failure)
    {
        if (footer is null) throw new ArgumentNullException(nameof(footer));
        if (rowCount < 0) throw new ArgumentOutOfRangeException(nameof(rowCount));

        if (rowCount != 0) return SqlSurfaceState.None;
        if (!string.IsNullOrEmpty(failure)) return SqlSurfaceState.Unreadable(failure);
        if (loading) return SqlSurfaceState.Loading;

        return footer.Kind == SqlMemoryFooterKind.Empty
            ? SqlSurfaceState.Empty(footer.Summary, footer.Hint ?? "")
            : SqlSurfaceState.None;
    }
}
