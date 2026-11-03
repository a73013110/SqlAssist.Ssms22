using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.Common;
using System.Threading;
using SqlAssist.Metadata.Caching;

namespace SqlAssist.Metadata.Querying;

/// <summary>
/// 這條連線看得到的一個資料庫；範圍列的資料庫清單與 SQL Search「全部」那一輪的名單用。
/// </summary>
/// <remarks>
/// 只有名稱、是不是系統資料庫、進不進得去與狀態。多的欄位（定序、相容性層級、大小）在這裡一個
/// 都用不到，而每多一欄就是清單查詢多一次跨資料庫的中繼資料讀取。
/// </remarks>
public sealed class SqlServerDatabase
{
    [Localizable(false)]
    public SqlServerDatabase(string name, bool isSystem, bool isAccessible = true, string state = OnlineState)
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

    /// <summary>四個系統資料庫的名稱；還沒問到清單、某個名稱已經被選起來時用來分段。</summary>
    /// <remarks>
    /// 有清單時一律照伺服器說的 <see cref="IsSystem"/>。四個名稱在每一版 SQL Server 上都相同，
    /// 分錯的代價也只是一個名字暫時排在另一段裡。
    /// </remarks>
    public static bool IsSystemName(string name) => SystemNames.Contains(name);

    private static readonly HashSet<string> SystemNames =
        new(new[] { "master", "model", "msdb", "tempdb" }, StringComparer.OrdinalIgnoreCase);

    public override string ToString() => Name;
}

/// <summary>
/// 向一台伺服器要它的資料庫清單。
/// </summary>
/// <remarks>
/// <b>這一支不建任何索引。</b>它只回答「有哪些可以選」，也是 SQL Search 範圍選「全部」時那一輪要搜的名單；
/// 建索引只發生在真的要搜某一個的時候。打開下拉就先把每一個都索引一遍是禁止的：共用主機上
/// 等於幾十次全表掃描，而使用者可能只是來挑一個。
/// </remarks>
public static class SqlServerDatabases
{
    /// <summary>清單查詢逾時；與搜尋索引同一個量級，一條輕查詢等不到這麼久就是伺服器有事。</summary>
    public const int DefaultCommandTimeoutSeconds = 60;

    /// <summary>
    /// 這條連線看得到的每一個資料庫，連同進不進得去。
    /// </summary>
    /// <remarks>
    /// 欄位順序：name、is_system、state_desc、is_accessible。
    ///
    /// 進不去的（離線、還原中、<c>HAS_DBACCESS</c> 為 0）<b>照樣回傳</b>並標出來：下拉只列
    /// 進得去的，但 SQL Search「全部」那一輪要把它們列進完整度，說出哪幾個沒搜到。在這裡直接濾掉的話，
    /// 畫面會說「已完整搜尋 38 個資料庫」，而使用者以為那是全部。
    /// <c>HAS_DBACCESS</c> 對離線的資料庫回傳 NULL，所以比較寫成 <c>= 1</c>。
    /// 這一族函式吃的是資料庫名稱而不是 object_id，不在「加不了限定字」那個坑裡。
    ///
    /// <c>database_id &lt;= 4</c> 就是 master／tempdb／model／msdb 四個；讓 UI 決定
    /// 要不要預設收起來，而不是在這裡直接不回傳——使用者確實會去搜 msdb 的作業指令碼。
    /// </remarks>
    public const string Query = @"
SELECT
    d.name,
    CASE WHEN d.database_id <= 4 THEN 1 ELSE 0 END AS is_system,
    d.state_desc,
    CASE WHEN d.state = 0 AND HAS_DBACCESS(d.name) = 1 THEN 1 ELSE 0 END AS is_accessible
FROM sys.databases AS d
ORDER BY d.name;";

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
    public static IReadOnlyList<SqlServerDatabase>? TryList(
        ISqlConnectionSource connectionSource,
        CancellationToken cancellationToken,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        if (connectionSource is null)
        {
            throw new ArgumentNullException(nameof(connectionSource));
        }

        try
        {
            using var connection = connectionSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = Query;
            command.CommandTimeout = commandTimeoutSeconds;

            var databases = new List<SqlServerDatabase>();
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // is_system 是查詢自己用 CASE 算出來的 int，不是目錄檢視上的 bit 欄位；
                // 用 GetBoolean 讀會拿到 InvalidCastException，而那不是 DbException，
                // 降級接不住。
                databases.Add(new SqlServerDatabase(
                    reader.GetString(0), reader.GetInt32(1) != 0, reader.GetInt32(3) != 0,
                    reader.IsDBNull(2) ? SqlServerDatabase.OnlineState : reader.GetString(2)));
            }

            return databases;
        }
        catch (DbException exception)
        {
            SqlMetadataFailure.Report("載入伺服器的資料庫清單：" + connectionSource.DatabaseName, exception);
            return null;
        }
    }
}
