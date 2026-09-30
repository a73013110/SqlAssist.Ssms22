using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Completion;

/// <summary>
/// 敘述裡一個資料來源解析出來的物件、它的欄位明細，以及明細是不是現成的。
/// </summary>
/// <remarks>
/// 「同名物件取哪一個、衍生資料表不查」這些規則只能有一份：欄位建議與
/// <c>SELECT *</c> 展開各自解析的話，同一個別名在兩個功能會指到不同的資料表。
/// </remarks>
public readonly struct SqlResolvedTable
{
    private SqlResolvedTable(SqlObjectInfo objectInfo, SqlObjectDetail? detail, bool detailWasCached)
    {
        Object = objectInfo;
        Detail = detail;
        DetailWasCached = detailWasCached;
    }

    public SqlObjectInfo Object { get; }

    public SqlObjectDetail? Detail { get; }

    /// <summary>明細在這次要求之前就已經在快取裡；只影響診斷紀錄怎麼寫。</summary>
    public bool DetailWasCached { get; }

    /// <summary>把資料來源解析成物件與欄位明細，允許查詢資料庫；查不到時回傳 null。</summary>
    /// <param name="catalog">目前這條連線的目錄；跨資料庫與跨伺服器的來源在這裡換過去。</param>
    public static async Task<SqlResolvedTable?> ResolveAsync(
        SqlMetadataCatalog? catalog,
        SqlTableReference? table,
        CancellationToken cancellationToken)
    {
        if (table is null || table.IsDerived)
        {
            return null;
        }

        catalog = SqlMetadataCatalogRegistry.Default.ScopeTo(catalog, table.Path);

        if (catalog is null)
        {
            return null;
        }

        // 走目錄那一支而不是自己比對快照：sys.triggers 這一類名稱的答案不在第一層，
        // 而那一份只有被指名時才載入。
        var matches = await catalog
            .FindObjectsAsync(table.ObjectName, table.SchemaName, cancellationToken)
            .ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return null;
        }

        var cached = catalog.TryGetCachedDetail(matches[0].ObjectId, out _);
        var detail = await catalog
            .GetDetailAsync(matches[0], cancellationToken, NotificationOrigin.Typing)
            .ConfigureAwait(false);
        return new SqlResolvedTable(matches[0], detail, cached);
    }

    /// <summary>
    /// 同一套解析規則的唯讀版本，只認快取裡現成的明細，絕不觸發查詢。
    /// </summary>
    /// <param name="catalog">目前這條連線的目錄；跨資料庫與跨伺服器的來源在這裡換過去。</param>
    public static bool TryPeek(SqlMetadataCatalog? catalog, SqlTableReference? table, out SqlResolvedTable resolved)
    {
        resolved = default;

        if (table is null || table.IsDerived)
        {
            return false;
        }

        catalog = SqlMetadataCatalogRegistry.Default.ScopeTo(catalog, table.Path);
        var snapshot = catalog?.CachedSnapshot;

        if (catalog is null || snapshot is null || snapshot.IsEmpty)
        {
            return false;
        }

        var matches = snapshot.Find(table.ObjectName, table.SchemaName);

        if (matches.Count == 0 || !catalog.TryGetCachedDetail(matches[0].ObjectId, out var detail))
        {
            return false;
        }

        resolved = new SqlResolvedTable(matches[0], detail, detailWasCached: true);
        return true;
    }
}
