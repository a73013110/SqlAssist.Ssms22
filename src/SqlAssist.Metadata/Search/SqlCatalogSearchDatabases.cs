using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 這條連線進得去的一個資料庫；搜尋範圍的多選清單用。
/// </summary>
/// <remarks>
/// 只有名稱與「是不是系統資料庫」兩件事。多的欄位（定序、相容性層級、大小）在這裡一個
/// 都用不到，而每多一欄就是清單查詢多一次跨資料庫的中繼資料讀取。
/// </remarks>
public sealed class SqlCatalogSearchDatabase
{
    public SqlCatalogSearchDatabase(string name, bool isSystem)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("資料庫名稱不可為空。", nameof(name));
        }

        Name = name;
        IsSystem = isSystem;
    }

    public string Name { get; }

    /// <summary>
    /// master／tempdb／model／msdb 四個。
    /// </summary>
    /// <remarks>
    /// 回報而不是直接濾掉：使用者確實會去搜 msdb 的作業指令碼，而「要不要預設收起來」
    /// 是呈現那一層的決定。在這裡濾掉的話，畫面上看不出那四個是被藏起來還是進不去。
    /// </remarks>
    public bool IsSystem { get; }

    public override string ToString() => IsSystem ? Name + "（系統）" : Name;
}

/// <summary>
/// 向一台伺服器要它的資料庫清單。
/// </summary>
/// <remarks>
/// <b>這一支不建任何索引。</b>它只回答「有哪些可以選」；建索引仍然只發生在使用者真的
/// 勾了某一個之後——把每一個進得去的資料庫都先索引一遍是明文禁止的，共用主機上等於
/// 幾十次全表掃描，而其中九成九不會有人搜。
/// </remarks>
public static class SqlCatalogSearchDatabases
{
    /// <summary>
    /// 列出清單；資料庫說不行時回傳 null。
    /// </summary>
    /// <remarks>
    /// 與索引那一層同一條降級規則：<see cref="DbException"/> 不冒出去，
    /// 失敗帶著「哪一條查詢」走 <see cref="SqlMetadataFailure"/>。冒出去會落在 Ssms22 的
    /// 平台邊界上，而它把每一次都記成一份完整堆疊。
    ///
    /// 回 null 而不是空清單：空清單的意思是「這台伺服器上一個都進不去」，
    /// 而那與「問不到」是兩件事——前者該顯示空狀態，後者該留著上一份清單。
    /// </remarks>
    public static IReadOnlyList<SqlCatalogSearchDatabase>? TryList(
        ISqlConnectionSource connectionSource,
        CancellationToken cancellationToken,
        int commandTimeoutSeconds = SqlCatalogSearchIndex.DefaultCommandTimeoutSeconds)
    {
        if (connectionSource is null)
        {
            throw new ArgumentNullException(nameof(connectionSource));
        }

        try
        {
            using var connection = connectionSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = SqlCatalogSearchQueries.Databases;
            command.CommandTimeout = commandTimeoutSeconds;

            var databases = new List<SqlCatalogSearchDatabase>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // is_system 是查詢自己用 CASE 算出來的 int，不是目錄檢視上的 bit 欄位；
                // 用 GetBoolean 讀會拿到 InvalidCastException，而那不是 DbException，
                // 降級接不住。
                databases.Add(new SqlCatalogSearchDatabase(reader.GetString(0), reader.GetInt32(1) != 0));
            }

            return databases;
        }
        catch (DbException exception)
        {
            SqlMetadataFailure.Report("載入搜尋範圍的資料庫清單：" + connectionSource.DatabaseName, exception);
            return null;
        }
    }
}
