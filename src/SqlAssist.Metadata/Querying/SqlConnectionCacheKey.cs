using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;

namespace SqlAssist.Metadata.Querying;

/// <summary>
/// 由連線字串與資料庫名稱組出穩定的快取鍵。
/// </summary>
/// <remarks>
/// 先前的實作以連線物件的識別雜湊當鍵，那個值會隨著重新連線而改變（造成無謂重查），
/// 也可能在不同物件之間碰撞（造成把 A 資料庫的物件餵給 B 資料庫的查詢視窗）。
/// 這裡改用正規化後的連線字串：去掉認證欄位、排序鍵值、統一大小寫。
/// </remarks>
public static class SqlConnectionCacheKey
{
    /// <summary>不可納入快取鍵的認證欄位。</summary>
    private static readonly HashSet<string> CredentialKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "password",
            "pwd",
            "accesstoken",
            "access token"
        };

    public static string Create(string? connectionString, string? databaseName)
    {
        return Compose(CreateServerKey(connectionString), databaseName);
    }

    /// <summary>只識別伺服器的鍵，不含資料庫。</summary>
    public static string CreateServerKey(string? connectionString)
    {
        return Normalize(connectionString);
    }

    /// <summary>
    /// 把伺服器鍵與資料庫名稱組成完整的快取鍵。
    /// </summary>
    /// <remarks>
    /// 組合規則只有這一份。跨資料庫建議要為同一條連線的另一個資料庫算鍵，
    /// 在呼叫端自己拼字串的話，拼法一旦與這裡分岔，同一個資料庫就會拿到兩份目錄
    /// ——症狀是查詢次數加倍，而兩份的過期時間各走各的。
    /// </remarks>
    public static string Compose(string? serverCacheKey, string? databaseName)
    {
        var builder = new StringBuilder();
        builder.Append(serverCacheKey ?? string.Empty);
        builder.Append('|');
        builder.Append((databaseName ?? string.Empty).ToLowerInvariant());
        return builder.ToString();
    }

    private static string Normalize(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return string.Empty;
        }

        try
        {
            var parsed = new DbConnectionStringBuilder { ConnectionString = connectionString };

            var pairs = parsed.Keys
                .Cast<string>()
                .Where(key => !CredentialKeys.Contains(key))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .Select(key => $"{key.ToLowerInvariant()}={Convert.ToString(parsed[key])?.ToLowerInvariant()}");

            return string.Join(";", pairs);
        }
        catch (ArgumentException)
        {
            // 連線字串格式無法解析時，退回整串比對即可；這裡的目的只是要一個穩定的鍵。
            return connectionString!.ToLowerInvariant();
        }
    }
}
