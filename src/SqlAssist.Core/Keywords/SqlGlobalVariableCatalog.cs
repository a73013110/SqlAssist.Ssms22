using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;

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
/// 把 32 個 <c>@@</c> 塞進每一次按鍵的候選清單，只會讓真正要找的東西更難找。
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
    private static readonly (string Name, Func<string> Description)[] Definitions =
    {
        // 系統函式
        ("@@ERROR", () => GlobalVariableText.Error),
        ("@@IDENTITY", () => GlobalVariableText.Identity),
        ("@@ROWCOUNT", () => GlobalVariableText.Rowcount),
        ("@@TRANCOUNT", () => GlobalVariableText.Trancount),

        // 資料指標
        ("@@CURSOR_ROWS", () => GlobalVariableText.CursorRows),
        ("@@FETCH_STATUS", () => GlobalVariableText.FetchStatus),

        // 中繼資料
        ("@@PROCID", () => GlobalVariableText.Procid),

        // 組態
        ("@@DATEFIRST", () => GlobalVariableText.Datefirst),
        ("@@DBTS", () => GlobalVariableText.Dbts),
        ("@@LANGID", () => GlobalVariableText.Langid),
        ("@@LANGUAGE", () => GlobalVariableText.Language),
        ("@@LOCK_TIMEOUT", () => GlobalVariableText.LockTimeout),
        ("@@MAX_CONNECTIONS", () => GlobalVariableText.MaxConnections),
        ("@@MAX_PRECISION", () => GlobalVariableText.MaxPrecision),
        ("@@NESTLEVEL", () => GlobalVariableText.Nestlevel),
        ("@@OPTIONS", () => GlobalVariableText.Options),
        ("@@SERVERNAME", () => GlobalVariableText.Servername),
        ("@@SERVICENAME", () => GlobalVariableText.Servicename),
        ("@@SPID", () => GlobalVariableText.Spid),
        ("@@TEXTSIZE", () => GlobalVariableText.Textsize),
        ("@@VERSION", () => GlobalVariableText.Version),

        // 系統統計
        ("@@CONNECTIONS", () => GlobalVariableText.Connections),
        ("@@CPU_BUSY", () => GlobalVariableText.CpuBusy),
        ("@@IDLE", () => GlobalVariableText.Idle),
        ("@@IO_BUSY", () => GlobalVariableText.IoBusy),
        ("@@PACKET_ERRORS", () => GlobalVariableText.PacketErrors),
        ("@@PACK_RECEIVED", () => GlobalVariableText.PackReceived),
        ("@@PACK_SENT", () => GlobalVariableText.PackSent),
        ("@@TIMETICKS", () => GlobalVariableText.Timeticks),
        ("@@TOTAL_ERRORS", () => GlobalVariableText.TotalErrors),
        ("@@TOTAL_READ", () => GlobalVariableText.TotalRead),
        ("@@TOTAL_WRITE", () => GlobalVariableText.TotalWrite)
    };

    private static readonly SqlLanguageCache<IReadOnlyList<SqlSuggestion>> SuggestionCache =
        new(_ => Build());

    /// <summary>
    /// 全域變數的建議項。
    /// </summary>
    /// <remarks>
    /// 插入文字含前面兩個小老鼠，而適用範圍也從第一個小老鼠開始算——
    /// 少了任何一邊，<c>@@ROW</c> 提交之後都會變成 <c>@@@@ROWCOUNT</c>。
    /// </remarks>
    public static IReadOnlyList<SqlSuggestion> All => SuggestionCache.Current;

    /// <summary>查出一個全域變數的一行說明；名稱含前面兩個小老鼠，大小寫不敏感。</summary>
    /// <remarks>
    /// 這一行是它們說明的唯一出處，滑鼠停留提示與建議清單問的是同一份
    /// （<see cref="SqlBuiltInDocCatalog"/>）。線性掃過的理由同
    /// <see cref="SqlDataTypeCatalog.TryGetDescription"/>。
    /// </remarks>
    public static bool TryGetDescription(string? name, out string description)
    {
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var (candidate, value) in Definitions)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                {
                    description = value();
                    return true;
                }
            }
        }

        description = string.Empty;
        return false;
    }

    private static IReadOnlyList<SqlSuggestion> Build()
    {
        var suggestions = new List<SqlSuggestion>(Definitions.Length);

        foreach (var (name, describe) in Definitions)
        {
            var description = describe();

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
