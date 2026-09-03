using System;
using System.Data;

namespace SqlAssist.Metadata.Querying;

/// <summary>
/// 把既有的連線來源指向<b>同一台伺服器的另一個資料庫</b>。
/// </summary>
/// <remarks>
/// 跨資料庫建議（<c>LibArchive.dbo.</c>）要的是另一個資料庫的物件清單，而查詢本身
/// 一律寫成不加限定的 <c>sys.objects</c>——換句話說，決定查哪一個資料庫的是連線，
/// 不是 SQL。因此這裡只換連線的資料庫，一整套查詢與快取邏輯都不必為跨資料庫
/// 再寫一份。
///
/// 刻意<b>不</b>釋放內層來源：它屬於查詢視窗，而同一個視窗可以同時指向好幾個
/// 資料庫。跟著釋放的症狀是使用者打完一次 <c>LibArchive.dbo.</c> 之後，
/// 原本那個資料庫的建議一起消失。
/// </remarks>
public sealed class SqlDatabaseScopedConnectionSource : ISqlConnectionSource
{
    private readonly ISqlConnectionSource _inner;

    public SqlDatabaseScopedConnectionSource(ISqlConnectionSource inner, string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new ArgumentException("資料庫名稱不可為空。", nameof(databaseName));
        }

        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        DatabaseName = databaseName;
        CacheKey = SqlConnectionCacheKey.Compose(inner.ServerCacheKey, databaseName);
    }

    public string CacheKey { get; }

    public string ServerCacheKey => _inner.ServerCacheKey;

    public string DatabaseName { get; }

    public IDbConnection OpenConnection()
    {
        var connection = _inner.OpenConnection();

        try
        {
            if (!string.Equals(connection.Database, DatabaseName, StringComparison.OrdinalIgnoreCase))
            {
                connection.ChangeDatabase(DatabaseName);
            }

            return connection;
        }
        catch
        {
            // 資料庫不存在、離線或這個登入進不去都會停在這裡。收掉開到一半的連線，
            // 讓例外照原樣往上走：那是 DbException，目錄那一層會降級成
            // 「這一輪沒有資料」，而吞掉的話呼叫端分不出是連線失敗還是真的沒有物件。
            connection.Dispose();
            throw;
        }
    }
}
