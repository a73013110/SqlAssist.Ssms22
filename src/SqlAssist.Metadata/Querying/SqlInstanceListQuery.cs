using System;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Notifications;

namespace SqlAssist.Metadata.Querying;

/// <summary>
/// 一份執行個體名單要問伺服器的兩條查詢，以及通知上的兩個標題。
/// </summary>
/// <remarks>
/// 名單本身的位置、排名與插入文字在 Core 的 <see cref="SqlInstanceList"/>；這裡只放
/// 中繼資料層才有的東西。名單查詢回傳一或兩個欄位：名稱，以及可有可無的列尾說明。
/// </remarks>
internal sealed class SqlInstanceListQuery
{
    private static readonly SqlInstanceListQuery Collation = new(
        SqlMetadataQueries.Collations,
        SqlMetadataQueries.DatabaseCollation,
        () => NotificationCatalog.LoadingCollations,
        () => NotificationCatalog.LoadingDatabaseCollation);

    private static readonly SqlInstanceListQuery Language = new(
        SqlMetadataQueries.Languages,
        SqlMetadataQueries.LoginLanguage,
        () => NotificationCatalog.LoadingLanguages,
        () => NotificationCatalog.LoadingLoginLanguage);

    private static readonly SqlInstanceListQuery TimeZone = new(
        SqlMetadataQueries.TimeZones,
        SqlMetadataQueries.ServerTimeZone,
        () => NotificationCatalog.LoadingTimeZones,
        () => NotificationCatalog.LoadingServerTimeZone);

    private readonly Func<string> _entriesTitle;
    private readonly Func<string> _inUseTitle;

    private SqlInstanceListQuery(string entries, string inUse, Func<string> entriesTitle, Func<string> inUseTitle)
    {
        Entries = entries;
        InUse = inUse;
        _entriesTitle = entriesTitle;
        _inUseTitle = inUseTitle;
    }

    /// <summary>名單；屬於伺服器，跨目錄共用。</summary>
    public string Entries { get; }

    /// <summary>在用的那一個；一列一欄，查不到時是 NULL。</summary>
    public string InUse { get; }

    public string EntriesTitle => _entriesTitle();

    public string InUseTitle => _inUseTitle();

    public static SqlInstanceListQuery For(SqlInstanceList list)
    {
        if (list is null)
        {
            throw new ArgumentNullException(nameof(list));
        }

        return list.Target switch
        {
            CompletionTarget.Collation => Collation,
            CompletionTarget.Language => Language,
            CompletionTarget.TimeZone => TimeZone,
            _ => throw new ArgumentOutOfRangeException(nameof(list), list.Target, null)
        };
    }
}
