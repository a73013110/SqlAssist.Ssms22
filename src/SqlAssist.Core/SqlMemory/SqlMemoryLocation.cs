using System;
using System.IO;

namespace SqlAssist.Core.SqlMemory;

/// <summary>SQL Memory 資料庫檔案在哪裡。</summary>
/// <remarks>
/// 放在 Core 讓擴充與工具（召回稽核唯讀讀取歷史）拿同一個路徑；各寫一份的話，搬家時工具安靜地讀不到東西。
/// </remarks>
public static class SqlMemoryLocation
{
    /// <summary>目前使用者的 SQL Memory 資料庫檔案。</summary>
    public static string DatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SqlAssist.Ssms22", "SQLMemory", "SQLMemory.db");
}
