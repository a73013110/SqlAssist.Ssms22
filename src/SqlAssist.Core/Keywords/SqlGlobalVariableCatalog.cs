using System.Collections.Generic;
using SqlAssist.Core.Completion;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// T-SQL 的全域變數（<c>@@ROWCOUNT</c>、<c>@@VERSION</c>…）。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlFunctionCatalog"/> 一樣只能手寫：這些名稱在文法上是變數而不是
/// 關鍵字，ScriptDom 的 token 列舉裡沒有它們，產生器撈不到。
///
/// 它們只在使用者打出 <c>@@</c> 之後出現（<see cref="CompletionTarget.GlobalVariable"/>），
/// 不混進一般清單：<c>@@</c> 開頭的名稱在 T-SQL 裡只有這一種意思，而反過來
/// 把 31 個 <c>@@</c> 塞進每一次按鍵的候選清單，只會讓真正要找的東西更難找。
///
/// 位置一律是 <see cref="SqlKeywordPosition.Any"/>，與內建函式不同。理由是使用者
/// 已經打出 <c>@@</c> 了——那個位置他要的百分之百是全域變數，此時再判一次位置，
/// 判對沒有好處（清單本來就只剩這一類），判錯的代價是清單整個空掉。
///
/// <c>@@REMSERVER</c> 刻意不收：它回報的遠端伺服器功能整個被拿掉了，
/// 打出來也得不到有意義的值。標準是「還有用就收，只是在說明欄標清楚」——
/// <see cref="SqlDataTypeCatalog"/> 收下已淘汰的 <c>TEXT</c> 就是同一個標準的另一面。
/// </remarks>
public static class SqlGlobalVariableCatalog
{
    /// <summary>名稱與說明；說明同時當成清單右側的提示。</summary>
    private static readonly (string Name, string Description)[] Definitions =
    {
        // 系統函式
        ("@@ERROR", "上一個敘述的錯誤代碼"),
        ("@@IDENTITY", "這個連線最後產生的識別值"),
        ("@@ROWCOUNT", "上一個敘述影響的資料列數"),
        ("@@TRANCOUNT", "目前連線的作用中交易數"),

        // 資料指標
        ("@@CURSOR_ROWS", "最後開啟的資料指標目前的資料列數"),
        ("@@FETCH_STATUS", "上一次 FETCH 的結果狀態"),

        // 中繼資料
        ("@@PROCID", "目前模組的 object_id"),

        // 組態
        ("@@DATEFIRST", "SET DATEFIRST 的目前值（一週的第一天）"),
        ("@@DBTS", "目前資料庫最後產生的 timestamp 值"),
        ("@@LANGID", "目前語言的識別碼"),
        ("@@LANGUAGE", "目前語言的名稱"),
        ("@@LOCK_TIMEOUT", "這個工作階段的鎖定逾時毫秒數"),
        ("@@MAX_CONNECTIONS", "允許的同時連線數上限"),
        ("@@MAX_PRECISION", "decimal 與 numeric 的有效位數上限"),
        ("@@NESTLEVEL", "目前模組的巢狀層級"),
        ("@@OPTIONS", "目前 SET 選項的位元遮罩"),
        ("@@SERVERNAME", "這台伺服器的名稱"),
        ("@@SERVICENAME", "這個執行個體的服務名稱"),
        ("@@SPID", "目前工作階段的識別碼"),
        ("@@TEXTSIZE", "SET TEXTSIZE 的目前值"),
        ("@@VERSION", "SQL Server 的版本、日期與作業系統"),

        // 系統統計
        ("@@CONNECTIONS", "啟動後嘗試連線的次數"),
        ("@@CPU_BUSY", "啟動後 CPU 的忙碌時間"),
        ("@@IDLE", "啟動後的閒置時間"),
        ("@@IO_BUSY", "啟動後花在輸入輸出的時間"),
        ("@@PACKET_ERRORS", "啟動後發生的封包錯誤數"),
        ("@@PACK_RECEIVED", "啟動後從網路讀取的封包數"),
        ("@@PACK_SENT", "啟動後寫到網路的封包數"),
        ("@@TIMETICKS", "每個時間刻度的微秒數"),
        ("@@TOTAL_ERRORS", "啟動後發生的磁碟讀寫錯誤數"),
        ("@@TOTAL_READ", "啟動後的磁碟讀取次數"),
        ("@@TOTAL_WRITE", "啟動後的磁碟寫入次數")
    };

    private static IReadOnlyList<SqlSuggestion>? _suggestions;

    private static readonly object Gate = new();

    /// <summary>
    /// 全域變數的建議項。
    /// </summary>
    /// <remarks>
    /// 插入文字含前面兩個小老鼠，而適用範圍也從第一個小老鼠開始算——
    /// 少了任何一邊，<c>@@ROW</c> 提交之後都會變成 <c>@@@@ROWCOUNT</c>。
    /// </remarks>
    public static IReadOnlyList<SqlSuggestion> All
    {
        get
        {
            lock (Gate)
            {
                return _suggestions ??= Build();
            }
        }
    }

    private static IReadOnlyList<SqlSuggestion> Build()
    {
        var suggestions = new List<SqlSuggestion>(Definitions.Length);

        foreach (var (name, description) in Definitions)
        {
            suggestions.Add(new SqlSuggestion(
                name,
                name,
                description,
                description,
                SuggestionKind.GlobalVariable));
        }

        return suggestions;
    }
}
