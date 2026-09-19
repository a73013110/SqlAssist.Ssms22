using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Search;

/// <summary>作業裡的一個步驟。</summary>
/// <remarks>
/// 不回指它所屬的作業：掃描一律從作業往下走，手上本來就有那一份。加一條回指的代價是
/// 兩個型別互相參照，而建構順序會從「照查詢結果組出來」變成「先組半份再補」。
/// </remarks>
public sealed class SqlAgentJobStep
{
    /// <param name="command">
    /// 步驟的命令本文；這一輪沒有撈第二段時為 null。<b>空字串與 null 是兩件事</b>：
    /// 前者是「這個步驟的命令真的是空的」，後者是「這一輪沒有問」。
    /// </param>
    public SqlAgentJobStep(
        int stepId, string name, string subsystem, string databaseName, string? command)
    {
        StepId = stepId;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Subsystem = subsystem ?? throw new ArgumentNullException(nameof(subsystem));
        DatabaseName = databaseName ?? throw new ArgumentNullException(nameof(databaseName));
        Command = command;
    }

    /// <summary><c>sysjobsteps.step_id</c>；在一個作業裡由 1 起算。</summary>
    public int StepId { get; }

    /// <summary>步驟名稱；沒有取名的步驟是空字串。</summary>
    public string Name { get; }

    /// <summary><c>TSQL</c>、<c>CmdExec</c>、<c>PowerShell</c>…；說不出來時是空字串。</summary>
    public string Subsystem { get; }

    /// <summary>步驟執行時所在的資料庫；只有 <c>TSQL</c> 子系統說得出來，其餘是空字串。</summary>
    public string DatabaseName { get; }

    /// <summary>命令本文；這一輪沒有撈第二段時為 null。</summary>
    public string? Command { get; }

    /// <summary>這個步驟的命令是 T-SQL 嗎；不是的話開進查詢視窗要整段換成註解。</summary>
    /// <remarks>
    /// 比對不分大小寫：<c>subsystem</c> 是 SQL Agent 自己寫進去的字串，而
    /// 目錄的定序可以是區分大小寫的——逐字比對的症狀是在那些執行個體上每一個步驟
    /// 都被當成非 T-SQL，整份指令碼變成註解。
    /// </remarks>
    public bool IsTransactSql => string.Equals(Subsystem, "TSQL", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Name.Length == 0 ? "#" + StepId : "#" + StepId + " " + Name;
}

/// <summary>一個 SQL Agent 作業，連同它看得到的步驟。</summary>
public sealed class SqlAgentJob
{
    public SqlAgentJob(Guid jobId, string name, bool isEnabled, IReadOnlyList<SqlAgentJobStep> steps)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("作業名稱不可為空。", nameof(name));
        }

        JobId = jobId;
        Name = name;
        IsEnabled = isEnabled;
        Steps = steps ?? throw new ArgumentNullException(nameof(steps));
    }

    /// <summary><c>sysjobs.job_id</c>；改名不會動到它。</summary>
    public Guid JobId { get; }

    public string Name { get; }

    public bool IsEnabled { get; }

    /// <summary>這個登入看得到的步驟，依 <c>step_id</c>；一個都沒有也是常態。</summary>
    public IReadOnlyList<SqlAgentJobStep> Steps { get; }

    public override string ToString() => Name;
}

/// <summary>
/// 一台伺服器上的作業快照。
/// </summary>
/// <remarks>
/// <b>鍵是伺服器，不是資料庫。</b>這是這個來源與目錄搜尋最根本的差別——目錄索引一個
/// 資料庫一份，作業則整台伺服器一份，因為 <c>msdb</c> 只有一個。以資料庫當鍵的症狀是
/// 使用者在同一台伺服器上換過三個資料庫之後，同一批作業被撈了三次。
///
/// 不做增量重新整理，與 <see cref="SqlCatalogSearchIndex"/> 刻意不同：那一邊非增量不可，
/// 是因為定義本文沒有上界；作業的資料量由作業數決定，整份重撈是兩條查詢。
/// 為它維護一套版本戳與合併邏輯，換到的是幾十毫秒，付出的是第二套會分岔的判斷。
///
/// <b>失敗不回半份。</b>任何一條查詢失敗都回 null（<see cref="TryLoad"/>），不是
/// 「作業有、命令沒有」——後者在畫面上與「這些步驟的命令裡真的沒有這個字」一模一樣。
/// </remarks>
public sealed class SqlAgentJobSearchSnapshot
{
    /// <summary>伺服器說不出自己叫什麼時用的名字。</summary>
    /// <remarks>
    /// <c>SERVERPROPERTY('ServerName')</c> 在正常的執行個體上不會是 NULL，但這個值會被寫進
    /// 去重鍵與膠囊，而兩者都不接受空字串。退成空字串的話，
    /// <see cref="SqlAgentJobSearchTarget"/> 的建構子會擲出
    /// <see cref="ArgumentException"/>——那不是 <see cref="DbException"/>，
    /// 降級接不住，而聚合器會把整個來源記成一次失敗。
    /// </remarks>
    public const string UnknownServerName = "(不明伺服器)";

    /// <summary>命令本文最多留這麼多字元。</summary>
    /// <remarks>
    /// 與 <see cref="SqlCatalogSearchIndex.DefaultMaxDefinitionBytes"/> 同一條理由，
    /// 只是量級小得多：整台伺服器的步驟命令加起來不像整個資料庫的模組定義那樣沒有上界，
    /// 但一個把幾 MB 的產生器輸出貼進步驟裡的作業就夠把它撐起來。超過之後不再收命令，
    /// 並把 <see cref="CommandsComplete"/> 標成 false，讓 provider 說得出「本文只掃到一部分」。
    /// 安靜地少一半是最糟的：使用者會以為那個字串在這台伺服器的作業裡不存在。
    /// </remarks>
    public const int DefaultMaxCommandCharacters = 8 * 1024 * 1024;

    private const string OpeningConnection = "開啟 SQL Agent 作業連線";
    private const string LoadingJobs = "載入 SQL Agent 作業清單";
    private const string LoadingCommands = "載入 SQL Agent 作業步驟命令";

    public SqlAgentJobSearchSnapshot(
        string serverName, IReadOnlyList<SqlAgentJob> jobs, bool includesCommands, bool commandsComplete)
    {
        if (string.IsNullOrEmpty(serverName))
        {
            throw new ArgumentException("伺服器名稱不可為空。", nameof(serverName));
        }

        ServerName = serverName;
        Jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        IncludesCommands = includesCommands;
        CommandsComplete = commandsComplete;
    }

    /// <summary>這一份是哪一台伺服器的。</summary>
    public string ServerName { get; }

    /// <summary>這個登入看得到的作業，依名稱。</summary>
    public IReadOnlyList<SqlAgentJob> Jobs { get; }

    /// <summary>這一份撈過第二段（命令本文）嗎。</summary>
    public bool IncludesCommands { get; }

    /// <summary>命令本文收齊了嗎；被 <see cref="DefaultMaxCommandCharacters"/> 擋住時為 false。</summary>
    public bool CommandsComplete { get; }

    /// <summary>
    /// 向一台伺服器要它的作業；資料庫說不行時回傳 null。
    /// </summary>
    /// <param name="includeCommands">
    /// 這一輪要不要命令本文。不要的話第二條查詢連送都不送，也不佔記憶體。
    /// </param>
    /// <remarks>
    /// 與目錄那一層同一條降級規則：<see cref="DbException"/> <b>不冒出去</b>，
    /// 失敗帶著「哪一條查詢」走 <see cref="SqlMetadataFailure"/>。冒出去會落在 Ssms22 的
    /// 平台邊界上，而它把每一次都記成一份完整堆疊——搜尋是打字驅動的，
    /// 一台讀不到 <c>msdb</c> 的伺服器等於使用者每多打一個字就多一份堆疊。
    ///
    /// 回 null 而不是空快照：空的意思是「這台伺服器上一個作業都沒有」，
    /// 而那與「讀不到」是兩件事，畫面上要說的話也不一樣。
    ///
    /// 取消<b>不</b>被當成資料庫失敗吞掉：<see cref="OperationCanceledException"/> 不是
    /// <see cref="DbException"/>，它會照常往外走，由聚合器當成正常流程處理。
    /// </remarks>
    public static SqlAgentJobSearchSnapshot? TryLoad(
        ISqlConnectionSource connectionSource,
        bool includeCommands,
        CancellationToken cancellationToken,
        int commandTimeoutSeconds = SqlCatalogSearchIndex.DefaultCommandTimeoutSeconds,
        int maxCommandCharacters = DefaultMaxCommandCharacters)
    {
        if (connectionSource is null)
        {
            throw new ArgumentNullException(nameof(connectionSource));
        }

        if (maxCommandCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCommandCharacters));
        }

        // 失敗訊息要指得出是哪一步；一路更新這個區域變數，比每一條查詢各包一層
        // try/catch 少三層縮排，而且不會有哪一條忘了包。
        var operation = OpeningConnection;

        try
        {
            using var connection = connectionSource.OpenConnection();

            operation = LoadingJobs;
            var rows = ReadJobs(connection, commandTimeoutSeconds, cancellationToken, out var serverName);

            var commands = EmptyCommands;
            var complete = true;

            if (includeCommands)
            {
                operation = LoadingCommands;
                commands = ReadCommands(
                    connection, commandTimeoutSeconds, maxCommandCharacters, cancellationToken, out complete);
            }

            return new SqlAgentJobSearchSnapshot(
                serverName, Compose(rows, includeCommands, commands), includeCommands, complete);
        }
        catch (DbException exception)
        {
            SqlMetadataFailure.Report(operation, exception);
            return null;
        }
    }

    private static readonly Dictionary<StepKey, string> EmptyCommands = new();

    /// <summary>把「一列一個步驟」的查詢結果併成「一個作業帶著它的步驟」。</summary>
    /// <remarks>
    /// 查詢已經 <c>ORDER BY j.name, s.step_id</c>，所以同一個作業的列是連在一起的；
    /// 仍然用字典認作業而不是「看到名字換了就換一份」，是因為兩個作業<b>可以同名</b>
    /// （<c>sysjobs</c> 上的唯一鍵是 job_id，名稱只在同一個分類裡唯一）。
    /// 照名字切的症狀是同名的兩個作業被併成一個，而其中一個的步驟掛到另一個身上。
    /// </remarks>
    private static IReadOnlyList<SqlAgentJob> Compose(
        List<JobRow> rows, bool includeCommands, Dictionary<StepKey, string> commands)
    {
        var jobs = new List<SqlAgentJob>();
        var steps = new Dictionary<Guid, List<SqlAgentJobStep>>();
        var order = new List<JobRow>();
        var seen = new HashSet<Guid>();

        foreach (var row in rows)
        {
            if (seen.Add(row.JobId))
            {
                order.Add(row);
                steps[row.JobId] = new List<SqlAgentJobStep>();
            }

            if (row.StepId is not { } stepId) continue;

            // 沒有撈第二段時 command 一律是 null（「沒有問」），不是空字串（「命令是空的」）。
            var command = includeCommands
                ? commands.TryGetValue(new StepKey(row.JobId, stepId), out var text) ? text : ""
                : null;

            steps[row.JobId].Add(
                new SqlAgentJobStep(stepId, row.StepName, row.Subsystem, row.StepDatabaseName, command));
        }

        foreach (var row in order)
        {
            jobs.Add(new SqlAgentJob(row.JobId, row.JobName, row.IsEnabled, steps[row.JobId].ToArray()));
        }

        return jobs.ToArray();
    }

    private static List<JobRow> ReadJobs(
        IDbConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken,
        out string serverName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SqlAgentJobSearchQueries.Jobs;
        command.CommandTimeout = commandTimeoutSeconds;

        var rows = new List<JobRow>();
        serverName = UnknownServerName;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var jobName = reader.GetString(1);

            // 名稱是必填的，而空名稱的作業建不出來；真的遇到就跳過那一列，
            // 不讓 SqlAgentJob 的建構子擲出一個降級接不住的 ArgumentException。
            if (jobName.Length == 0) continue;

            rows.Add(new JobRow(
                reader.GetGuid(0),
                jobName,
                !reader.IsDBNull(2) && reader.GetBoolean(2),
                reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
                ReadText(reader, 4),
                ReadText(reader, 5),
                ReadText(reader, 6)));

            if (!reader.IsDBNull(7) && reader.GetString(7) is { Length: > 0 } name) serverName = name;
        }

        return rows;
    }

    /// <remarks>
    /// 超過上限之後<b>不</b>提早離開讀取迴圈：連線上還有沒讀完的結果集，丟著走等於
    /// 下一次用這條連線的人拿到別人的資料列。照常讀完，只是不再留下命令本文。
    /// </remarks>
    private static Dictionary<StepKey, string> ReadCommands(
        IDbConnection connection,
        int commandTimeoutSeconds,
        int maxCommandCharacters,
        CancellationToken cancellationToken,
        out bool complete)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SqlAgentJobSearchQueries.StepCommands;
        command.CommandTimeout = commandTimeoutSeconds;

        var commands = new Dictionary<StepKey, string>();
        var characters = 0L;
        complete = true;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = ReadText(reader, 2);

            if (characters + text.Length > maxCommandCharacters)
            {
                complete = false;
                continue;
            }

            characters += text.Length;
            commands[new StepKey(reader.GetGuid(0), reader.GetInt32(1))] = text;
        }

        return commands;
    }

    /// <summary>可為 NULL 的字串欄位；NULL 一律讀成空字串。</summary>
    /// <remarks>
    /// <c>step_name</c>、<c>subsystem</c>、<c>database_name</c> 與 <c>command</c> 在
    /// <c>sysjobsteps</c> 上都可以是 NULL（沒有步驟的作業走 <c>LEFT JOIN</c> 更是整排 NULL）。
    /// 直接 <c>GetString</c> 會拿到 <see cref="InvalidCastException"/>，而那不是
    /// <see cref="DbException"/>，降級接不住。
    /// </remarks>
    private static string ReadText(IDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);

    /// <summary>查詢回來的一列：一個作業加上它的其中一個步驟（沒有步驟時後半是空的）。</summary>
    private readonly struct JobRow
    {
        internal JobRow(
            Guid jobId,
            string jobName,
            bool isEnabled,
            int? stepId,
            string stepName,
            string subsystem,
            string stepDatabaseName)
        {
            JobId = jobId;
            JobName = jobName;
            IsEnabled = isEnabled;
            StepId = stepId;
            StepName = stepName;
            Subsystem = subsystem;
            StepDatabaseName = stepDatabaseName;
        }

        internal Guid JobId { get; }

        internal string JobName { get; }

        internal bool IsEnabled { get; }

        internal int? StepId { get; }

        internal string StepName { get; }

        internal string Subsystem { get; }

        internal string StepDatabaseName { get; }
    }

    /// <summary>命令本文的字典鍵：哪一個作業的第幾步。</summary>
    /// <remarks>
    /// <c>step_id</c> 只在一個作業裡唯一，單獨拿它當鍵會讓每一個作業的第一步互相覆蓋。
    /// </remarks>
    private readonly struct StepKey : IEquatable<StepKey>
    {
        private readonly Guid _jobId;
        private readonly int _stepId;

        internal StepKey(Guid jobId, int stepId)
        {
            _jobId = jobId;
            _stepId = stepId;
        }

        public bool Equals(StepKey other) => _stepId == other._stepId && _jobId == other._jobId;

        public override bool Equals(object? obj) => obj is StepKey other && Equals(other);

        public override int GetHashCode() => unchecked(_jobId.GetHashCode() * 397 ^ _stepId);
    }
}

/// <summary>
/// 以<b>伺服器</b>快取鍵存放的作業快照。
/// </summary>
/// <remarks>
/// 鍵走 <see cref="ISqlConnectionSource.ServerCacheKey"/> 而不是
/// <see cref="ISqlConnectionSource.CacheKey"/>，這是整個來源最容易寫錯的一行：
/// 後者含資料庫名稱，而作業與目前連在哪一個資料庫無關。用錯的症狀不是錯誤而是浪費
/// ——使用者在同一台伺服器上換一次資料庫，整批作業就重撈一次，而畫面上只看得出「搜尋變慢了」。
///
/// 份數上限而不是位元組上限，與 <see cref="SqlCatalogSearchIndexCache"/> 相反：
/// 那一邊一份索引的大小差到三個數量級（定義本文沒有上界），所以只能算位元組；
/// 這一邊單份的上限由 <see cref="SqlAgentJobSearchSnapshot.DefaultMaxCommandCharacters"/>
/// 擋住了，剩下要擋的只有「使用者在下拉裡換過幾台伺服器」。
///
/// <b>失敗不進快取。</b>否則權限恢復或連線回來之後仍然拿到「沒有資料」。
/// </remarks>
public sealed class SqlAgentJobSearchSnapshotCache
{
    /// <summary>同時留幾台伺服器的快照。</summary>
    public const int DefaultMaxEntries = 4;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _buildGates = new(StringComparer.Ordinal);
    private long _clock;
    private int _loads;

    public SqlAgentJobSearchSnapshotCache(int maxEntries = DefaultMaxEntries)
    {
        if (maxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        MaxEntries = maxEntries;
    }

    public int MaxEntries { get; }

    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>向伺服器要過幾次快照。</summary>
    /// <remarks>
    /// 「有沒有重撈」靠這個數字，不靠結果筆數：命中快取與重新撈一次在結果上一模一樣。
    /// </remarks>
    public int Loads
    {
        get
        {
            lock (_gate) return _loads;
        }
    }

    /// <summary>已經有一份可以直接用的快照嗎。</summary>
    public bool IsFresh(string serverCacheKey)
    {
        if (serverCacheKey is null) throw new ArgumentNullException(nameof(serverCacheKey));

        lock (_gate) return _entries.ContainsKey(serverCacheKey);
    }

    public bool TryGet(string serverCacheKey, out SqlAgentJobSearchSnapshot? snapshot)
    {
        if (serverCacheKey is null) throw new ArgumentNullException(nameof(serverCacheKey));

        lock (_gate)
        {
            if (_entries.TryGetValue(serverCacheKey, out var cached))
            {
                snapshot = cached.Snapshot;
                return true;
            }

            snapshot = null;
            return false;
        }
    }

    /// <summary>
    /// 拿這台伺服器的快照，沒有就撈一份；資料庫說不行時回傳 null。
    /// </summary>
    /// <remarks>
    /// <b>鎖只鎖同一台伺服器。</b>整份快取一把鎖的症狀在這個來源上不明顯（一輪只問一台），
    /// 但把它與目錄那一份寫成不同的規矩，下一個人得先讀完兩份才知道哪一份是對的。
    /// 鎖的順序永遠是「先拿鍵鎖、再拿 <see cref="_gate"/>」，不會反過來——反過來的一條路徑
    /// 就是一個死結，而死結在搜尋上的症狀是工具窗整個不動，沒有任何訊息。
    ///
    /// 手上那一份沒有命令本文而這一輪要時，<b>整份重撈</b>：作業的資料量小，
    /// 兩條查詢比「只補第二段」那套合併邏輯便宜，也少一份會分岔的判斷。
    /// </remarks>
    public SqlAgentJobSearchSnapshot? GetOrLoad(
        ISqlConnectionSource connectionSource, bool includeCommands, CancellationToken cancellationToken)
    {
        if (connectionSource is null)
        {
            throw new ArgumentNullException(nameof(connectionSource));
        }

        var key = connectionSource.ServerCacheKey;
        object gate;

        lock (_gate)
        {
            if (TryUse(key, includeCommands, out var ready)) return ready;
            gate = BuildGate(key);
        }

        lock (gate)
        {
            lock (_gate)
            {
                // 在鎖後面等的那段時間，先到的那一條可能已經撈好了。
                if (TryUse(key, includeCommands, out var ready)) return ready;
                _loads++;
            }

            return Store(key, SqlAgentJobSearchSnapshot.TryLoad(
                connectionSource, includeCommands, cancellationToken));
        }
    }

    /// <summary>
    /// 整批丟掉；使用者按重新整理，或換了一條連線。
    /// </summary>
    /// <remarks>
    /// 不像目錄那一邊分成「標記過期」與「整批丟掉」兩種：那一邊標記過期換到的是
    /// 沿著版本戳只重撈變更過的東西，而這一邊本來就整份重撈，兩條路的成本一樣。
    /// 多一種只是多一個要解釋的狀態。
    ///
    /// 建置鎖<b>不</b>跟著清掉：正在撈的那一條執行緒手上還握著它，換一顆新的等於同一台
    /// 伺服器可以被兩條執行緒同時撈。
    /// </remarks>
    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    /// <summary>呼叫端必須持有 <see cref="_gate"/>。</summary>
    private bool TryUse(string key, bool includeCommands, out SqlAgentJobSearchSnapshot? snapshot)
    {
        snapshot = null;

        if (!_entries.TryGetValue(key, out var cached)) return false;

        // 手上這一份沒有命令本文，而這一輪要：這不算命中。
        if (includeCommands && !cached.Snapshot.IncludesCommands) return false;

        cached.UsedAt = ++_clock;
        snapshot = cached.Snapshot;
        return true;
    }

    /// <summary>呼叫端必須持有 <see cref="_gate"/>。</summary>
    private object BuildGate(string key)
    {
        if (_buildGates.TryGetValue(key, out var gate)) return gate;

        gate = new object();
        _buildGates[key] = gate;
        return gate;
    }

    private SqlAgentJobSearchSnapshot? Store(string key, SqlAgentJobSearchSnapshot? snapshot)
    {
        if (snapshot is null) return null;

        lock (_gate)
        {
            _entries[key] = new Entry(snapshot, ++_clock);
            EvictExcess();
        }

        return snapshot;
    }

    /// <remarks>掃一遍找最舊的就夠：份數上限是個位數，而一輪搜尋最多跑一次。</remarks>
    private void EvictExcess()
    {
        while (_entries.Count > MaxEntries)
        {
            string? oldestKey = null;
            var oldestUsedAt = long.MaxValue;

            foreach (var pair in _entries)
            {
                if (pair.Value.UsedAt >= oldestUsedAt) continue;

                oldestUsedAt = pair.Value.UsedAt;
                oldestKey = pair.Key;
            }

            if (oldestKey is null) return;

            _entries.Remove(oldestKey);
        }
    }

    private sealed class Entry
    {
        internal Entry(SqlAgentJobSearchSnapshot snapshot, long usedAt)
        {
            Snapshot = snapshot;
            UsedAt = usedAt;
        }

        internal SqlAgentJobSearchSnapshot Snapshot { get; }

        /// <summary>最後一次被讀到是第幾號動作；淘汰時比這個。</summary>
        internal long UsedAt { get; set; }
    }
}
