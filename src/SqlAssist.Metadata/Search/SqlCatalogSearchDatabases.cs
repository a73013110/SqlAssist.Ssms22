using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Threading;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 這條連線看得到的一個資料庫；搜尋範圍的多選清單與「全部」那一輪的名單用。
/// </summary>
/// <remarks>
/// 只有名稱、是不是系統資料庫、進不進得去與狀態。多的欄位（定序、相容性層級、大小）在這裡一個
/// 都用不到，而每多一欄就是清單查詢多一次跨資料庫的中繼資料讀取。
/// </remarks>
public sealed class SqlCatalogSearchDatabase
{
    [Localizable(false)]
    public SqlCatalogSearchDatabase(string name, bool isSystem, bool isAccessible = true, string state = OnlineState)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("資料庫名稱不可為空。", nameof(name));
        }

        Name = name;
        IsSystem = isSystem;
        IsAccessible = isAccessible;
        State = string.IsNullOrEmpty(state) ? OnlineState : state;
    }

    /// <summary><c>sys.databases.state_desc</c> 的線上值。</summary>
    public const string OnlineState = "ONLINE";

    public string Name { get; }

    /// <summary>
    /// master／tempdb／model／msdb 四個。
    /// </summary>
    /// <remarks>
    /// 回報而不是直接濾掉：使用者確實會去搜 msdb 的作業指令碼，而「要不要預設收起來」
    /// 是呈現那一層的決定。在這裡濾掉的話，畫面上看不出那四個是被藏起來還是進不去。
    /// </remarks>
    public bool IsSystem { get; }

    /// <summary>
    /// 在線上而且這個登入進得去。
    /// </summary>
    /// <remarks>
    /// 進不去的也回報：多選清單只列進得去的，但「全部」那一輪要把它們寫進完整度，
    /// 否則畫面說「已完整搜尋」而少了使用者以為有搜的那幾個。
    /// </remarks>
    public bool IsAccessible { get; }

    /// <summary>伺服器說的狀態（<c>ONLINE</c>、<c>OFFLINE</c>、<c>RESTORING</c>…），原樣給人看，不翻譯。</summary>
    public string State { get; }

    /// <summary>在線上卻進不去：這個登入沒有權限。</summary>
    public bool IsDenied => !IsAccessible && string.Equals(State, OnlineState, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => IsSystem ? SearchSourceText.SystemDatabase(Name) : Name;
}

/// <summary>
/// 向一台伺服器要它的資料庫清單。
/// </summary>
/// <remarks>
/// <b>這一支不建任何索引。</b>它只回答「有哪些可以選」，也是範圍選「全部」時那一輪要搜的名單；
/// 建索引只發生在真的要搜某一個的時候。打開下拉就先把每一個都索引一遍是禁止的：共用主機上
/// 等於幾十次全表掃描，而使用者可能只是來挑一個。
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
                databases.Add(new SqlCatalogSearchDatabase(
                    reader.GetString(0), reader.GetInt32(1) != 0, reader.GetInt32(3) != 0,
                    reader.IsDBNull(2) ? SqlCatalogSearchDatabase.OnlineState : reader.GetString(2)));
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
