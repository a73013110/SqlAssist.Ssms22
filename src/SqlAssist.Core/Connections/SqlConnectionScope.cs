using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Connections;

/// <summary>
/// 範圍列上伺服器與資料庫兩個篩選的值；SQL Memory 與 SQL Search 共用這一份規則。
/// </summary>
/// <remarks>
/// 兩個篩選都是<b>明確的值</b>，沒有「跟著查詢視窗」這種會自己變的狀態：切分頁、在查詢視窗換連線
/// 都不動它，只有使用者勾選或按「套用查詢視窗的連線」（<see cref="Apply"/>）才換。跟著走的那一版，
/// 使用者盯著同一組篩選，搜到的卻隨著他剛點過哪個分頁而不同。
///
/// 資料庫空名單就是「全部」。伺服器空名單在多選時也是「全部」；單選（SQL Search 換一台換的是
/// 整份目錄）時是「還沒選」，由宿主說那一句。
///
/// 名稱怎麼比由宿主給：SQL Memory 比的是儲存層自己回的字（ordinal），SQL Search 比的是伺服器上的
/// 名稱（大小寫由定序決定，一律不分大小寫）。
/// </remarks>
public sealed class SqlConnectionScope
{
    private readonly List<string> _servers = new();
    private readonly List<string> _databases = new();
    private readonly StringComparer _comparer;

    /// <param name="multipleServers">伺服器可以複選；false 時勾一台就換掉上一台，取消勾不算數。</param>
    /// <param name="comparer">名稱比對規則；伺服器與資料庫共用。</param>
    public SqlConnectionScope(bool multipleServers, StringComparer comparer)
    {
        MultipleServers = multipleServers;
        _comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
    }

    public bool MultipleServers { get; }

    /// <summary>勾起來的伺服器，依勾選順序；空表示全部（多選）或還沒選（單選）。</summary>
    public IReadOnlyList<string> Servers => _servers;

    /// <summary>勾起來的資料庫，依勾選順序；空表示全部。</summary>
    public IReadOnlyList<string> Databases => _databases;

    public bool IsServerSelected(string server) => IndexOf(_servers, server) >= 0;

    public bool IsDatabaseSelected(string database) => IndexOf(_databases, database) >= 0;

    /// <summary>勾或取消勾一台伺服器。</summary>
    /// <returns>true 表示條件真的變了。</returns>
    /// <remarks>
    /// 伺服器一換就把資料庫清掉：資料庫名稱是每台自己的，留著上一台的名單會篩成一列都沒有，
    /// 或拿上一台的名稱搜這一台、每個都回「不存在」，而畫面上看不出是上一台的條件還掛著。
    ///
    /// 單選時取消勾不算數：勾掉唯一那一台等於沒有範圍可搜，面板的 radio 本來也取消不掉。
    /// </remarks>
    public bool SetServerSelected(string server, bool selected)
    {
        RequireName(server, nameof(server));
        var index = IndexOf(_servers, server);

        if (!MultipleServers)
        {
            if (!selected || (index >= 0 && _servers.Count == 1)) return false;
            _servers.Clear();
            _servers.Add(server);
            _databases.Clear();
            return true;
        }

        if (selected == index >= 0) return false;
        if (selected) _servers.Add(server);
        else _servers.RemoveAt(index);
        _databases.Clear();
        return true;
    }

    /// <summary>勾或取消勾一個資料庫。</summary>
    /// <returns>true 表示條件真的變了。</returns>
    public bool SetDatabaseSelected(string database, bool selected)
    {
        RequireName(database, nameof(database));
        var index = IndexOf(_databases, database);
        if (selected == index >= 0) return false;
        if (selected) _databases.Add(database);
        else _databases.RemoveAt(index);
        return true;
    }

    /// <summary>清掉伺服器；資料庫跟著清，理由同 <see cref="SetServerSelected"/>。</summary>
    public bool ClearServers()
    {
        if (_servers.Count == 0) return false;
        _servers.Clear();
        _databases.Clear();
        return true;
    }

    /// <summary>回到「全部資料庫」。</summary>
    public bool ClearDatabases()
    {
        if (_databases.Count == 0) return false;
        _databases.Clear();
        return true;
    }

    /// <summary>
    /// 套用查詢視窗的連線：兩個篩選一次換成那一台與那一個資料庫。
    /// </summary>
    /// <returns>false 表示連線不完整，原條件不變；那一句由宿主說，兩個工具窗同一句。</returns>
    /// <remarks>
    /// 取代而不是加進去：這顆按鈕說的是「只看我現在連的那一個」，加上去的那一版按幾次之後
    /// 名單愈來愈長，而使用者以為自己每次都縮小了範圍。
    /// </remarks>
    public bool Apply(SqlConnectionLabel? connection)
    {
        if (connection is null || string.IsNullOrEmpty(connection.Server) || string.IsNullOrEmpty(connection.Database))
            return false;

        _servers.Clear();
        _servers.Add(connection.Server);
        _databases.Clear();
        _databases.Add(connection.Database);
        return true;
    }

    private int IndexOf(List<string> names, string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        return names.FindIndex(candidate => _comparer.Equals(candidate, name));
    }

    private static void RequireName(string name, string parameter)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("名稱不可為空。", parameter);
    }
}
