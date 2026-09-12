using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.Ssms22.QueryMemory;

/// <summary>
/// 每個查詢視窗最後一次已知的伺服器與資料庫。
/// </summary>
/// <remarks>
/// 只由 SSMS 的連線事件寫入，擷取時只讀這裡。<c>ISqlEditorService.GetCurrentConnection()</c>
/// 有 UI 執行緒相依性，實測塞住時 1908 ms；按下執行的那一刻去問，等於把那筆延遲
/// 加在使用者的 F5 上。連線事件本來就在 SSMS 自己更新完 UI 之後發出，那一刻問最便宜。
///
/// 只留伺服器與資料庫名稱：完整連線字串、密碼與 Token 一律不進 Query Memory。
/// 還沒收到任何連線事件的視窗就是沒有連線內容，擷取仍照常進行——SQL 本身才是主體。
/// </remarks>
internal static class QueryMemoryConnections
{
    /// <summary>連線字串裡代表伺服器的鍵；<c>DbConnectionStringBuilder</c> 不會替我們正規化同義字。</summary>
    private static readonly string[] ServerKeys = { "Data Source", "Server", "Address", "Addr", "Network Address" };

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, QueryConnectionContext> ByMoniker =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SSMS 說某個查詢視窗的連線變了；<paramref name="connection"/> 為 null 表示已中斷。</summary>
    public static void Note(string? moniker, IDbConnection? connection)
    {
        if (string.IsNullOrEmpty(moniker)) return;

        var context = Describe(connection);

        lock (SyncRoot)
        {
            if (context is null) ByMoniker.Remove(moniker!);
            else ByMoniker[moniker!] = context;
        }
    }

    public static QueryConnectionContext? Get(string? moniker)
    {
        if (string.IsNullOrEmpty(moniker)) return null;

        lock (SyncRoot)
        {
            return ByMoniker.TryGetValue(moniker!, out var context) ? context : null;
        }
    }

    /// <summary>視窗關掉之後不必再留；識別碼會被 SSMS 重複使用。</summary>
    public static void Forget(string? moniker)
    {
        if (string.IsNullOrEmpty(moniker)) return;

        lock (SyncRoot)
        {
            ByMoniker.Remove(moniker!);
        }
    }

    private static QueryConnectionContext? Describe(IDbConnection? connection)
    {
        if (connection is null) return null;

        var server = ServerName(connection.ConnectionString);
        var database = connection.Database ?? string.Empty;
        return string.IsNullOrEmpty(server) && string.IsNullOrEmpty(database)
            ? null
            : new QueryConnectionContext(server, database);
    }

    private static string ServerName(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return string.Empty;

        try
        {
            var parsed = new DbConnectionStringBuilder { ConnectionString = connectionString };

            foreach (var key in ServerKeys)
            {
                if (parsed.TryGetValue(key, out var value) && Convert.ToString(value) is { Length: > 0 } server)
                {
                    return server;
                }
            }
        }
        // 連線字串解析不了就沒有伺服器名稱可寫；不猜，也不把整串存下來。
        catch (ArgumentException) { }

        return string.Empty;
    }
}
