using System;
using System.Collections.Generic;

namespace SqlAssist.Metadata;

/// <summary>
/// 依快取鍵共用 <see cref="SqlMetadataCatalog"/>。
/// </summary>
/// <remarks>
/// 先前每個查詢視窗各自建立一份中繼資料提供者，同一個資料庫開了五個分頁
/// 就會查五次。改為以「伺服器＋資料庫」為單位共用，開新分頁時可以直接命中快取。
/// </remarks>
public sealed class SqlMetadataCatalogRegistry
{
    public static readonly SqlMetadataCatalogRegistry Default = new();

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, SqlMetadataCatalog> _catalogs =
        new(StringComparer.Ordinal);

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);

    public int CommandTimeoutSeconds { get; set; } = 15;

    public SqlMetadataCatalog GetOrCreate(ISqlConnectionSource connectionSource)
    {
        if (connectionSource is null)
        {
            throw new ArgumentNullException(nameof(connectionSource));
        }

        lock (_syncRoot)
        {
            if (_catalogs.TryGetValue(connectionSource.CacheKey, out var existing))
            {
                // 已經有目錄在用同一個連線，這一份不會有人用到。
                // 由這裡負責釋放，呼叫端就不必判斷自己交出去的那份還在不在被共用——
                // 先前呼叫端會自行釋放，結果關掉一個查詢視窗就讓其他視窗的目錄失效。
                (connectionSource as IDisposable)?.Dispose();
                return existing;
            }

            var created = new SqlMetadataCatalog(connectionSource, Lifetime, CommandTimeoutSeconds);
            _catalogs[connectionSource.CacheKey] = created;
            return created;
        }
    }

    /// <summary>清空所有目錄的快取，但保留實例，避免正在使用的呼叫端拿到孤兒物件。</summary>
    public void InvalidateAll()
    {
        lock (_syncRoot)
        {
            foreach (var catalog in _catalogs.Values)
            {
                catalog.Invalidate();
            }
        }
    }
}
