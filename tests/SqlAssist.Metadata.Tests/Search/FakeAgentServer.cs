using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using SqlAssist.Metadata.Querying;
using SqlAssist.Metadata.Search;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// 一台假的伺服器，上面有 SQL Agent 作業。
/// </summary>
/// <remarks>
/// 與 <see cref="FakeCatalogServer"/> 分開而不是加在它身上：作業來源的形狀刻意與目錄不同
/// ——它<b>不換目錄</b>（查詢寫三段式的 <c>msdb.dbo.</c>），而快取鍵是伺服器不是資料庫。
/// 合成一份假物件的話，「換了資料庫不該重撈作業」這一條會量到目錄那一份的行為。
///
/// <see cref="MsdbDenied"/> 是這一組測試的重點：多數登入對 <c>msdb</c> 沒有
/// <c>SELECT</c>，而真實伺服器在那時候回的是 <see cref="System.Data.Common.DbException"/>
/// ——也就是必須被降級成「這個來源這一輪沒有資料」的那一族。
/// </remarks>
internal sealed class FakeAgentServer
{
    private readonly List<FakeAgentJob> _jobs = new();

    internal FakeAgentServer(string serverKey = "server-a", string? serverName = "LIBSQL01")
    {
        ServerKey = serverKey;
        ServerName = serverName;
    }

    internal string ServerKey { get; }

    /// <summary><c>SERVERPROPERTY('ServerName')</c> 的答案；null 表示伺服器說不出來。</summary>
    internal string? ServerName { get; set; }

    /// <summary>這個登入對 <c>msdb</c> 沒有權限；每一條查詢都失敗。</summary>
    internal bool MsdbDenied { get; set; }

    /// <summary>連線本身開不起來。</summary>
    internal bool FailsOnOpen { get; set; }

    /// <summary>開過幾次連線；有沒有重撈靠這個數字，不靠結果筆數。</summary>
    internal int Opened { get; private set; }

    /// <summary>每一次執行的命令原文，順序照執行的先後。</summary>
    internal List<string> Commands { get; } = new();

    internal IReadOnlyList<FakeAgentJob> Jobs => _jobs;

    internal FakeAgentJob AddJob(string name, bool enabled = true)
    {
        var job = new FakeAgentJob(NextJobId(), name, enabled);
        _jobs.Add(job);
        return job;
    }

    /// <summary>
    /// 可預期的 job_id。
    /// </summary>
    /// <remarks>
    /// <see cref="Guid.NewGuid"/> 會讓去重鍵每一次執行都不一樣，而那幾條斷言正是要
    /// 逐字比對鍵的內容。
    /// </remarks>
    private Guid NextJobId() =>
        new(_jobs.Count + 1, 0, 0, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });

    /// <param name="databaseName">
    /// 連線目前在哪一個資料庫。作業查詢寫的是三段式名稱，所以這個值<b>不該</b>影響結果——
    /// 而那正是「快取鍵是伺服器不是資料庫」那幾條測試要量的東西。
    /// </param>
    internal ISqlConnectionSource SourceFor(string databaseName = "Library") =>
        new FakeAgentConnectionSource(this, databaseName);

    internal IDbConnection Open()
    {
        Opened++;

        if (FailsOnOpen) throw new UnreachableServerException();

        return new FakeAgentConnection(this);
    }

    internal void Record(string commandText) => Commands.Add(commandText);

    /// <summary>某一條查詢被執行過幾次；「第二段有沒有被送出去」靠它。</summary>
    internal int CountCommands(string fragment)
    {
        var count = 0;

        foreach (var command in Commands)
        {
            if (command.IndexOf(fragment, StringComparison.Ordinal) >= 0) count++;
        }

        return count;
    }
}

internal sealed class FakeAgentJob
{
    internal FakeAgentJob(Guid jobId, string name, bool enabled)
    {
        JobId = jobId;
        Name = name;
        Enabled = enabled;
    }

    internal Guid JobId { get; }

    internal string Name { get; }

    internal bool Enabled { get; }

    internal List<FakeAgentStep> Steps { get; } = new();

    internal FakeAgentJob WithStep(
        int stepId, string name, string command, string subsystem = "TSQL", string databaseName = "Library")
    {
        Steps.Add(new FakeAgentStep(stepId, name, subsystem, databaseName, command));
        return this;
    }
}

internal sealed class FakeAgentStep
{
    internal FakeAgentStep(int stepId, string name, string subsystem, string databaseName, string command)
    {
        StepId = stepId;
        Name = name;
        Subsystem = subsystem;
        DatabaseName = databaseName;
        Command = command;
    }

    internal int StepId { get; }

    internal string Name { get; }

    internal string Subsystem { get; }

    internal string DatabaseName { get; }

    internal string Command { get; }
}

/// <summary>
/// 指向這台伺服器的連線來源。
/// </summary>
/// <remarks>
/// <see cref="CacheKey"/> 含資料庫、<see cref="ServerCacheKey"/> 不含，與產品一致。
/// 兩者做成同一個字串的話，「換了資料庫不該重撈作業」永遠會通過。
/// </remarks>
internal sealed class FakeAgentConnectionSource : ISqlConnectionSource
{
    private readonly FakeAgentServer _server;

    internal FakeAgentConnectionSource(FakeAgentServer server, string databaseName)
    {
        _server = server;
        DatabaseName = databaseName;
        CacheKey = SqlConnectionCacheKey.Compose(server.ServerKey, databaseName);
    }

    public string CacheKey { get; }

    public string ServerCacheKey => _server.ServerKey;

    public string DatabaseName { get; }

    public IDbConnection OpenConnection() => _server.Open();
}

internal sealed class FakeAgentConnection : IDbConnection
{
    private readonly FakeAgentServer _server;

    internal FakeAgentConnection(FakeAgentServer server) => _server = server;

    [AllowNull] public string ConnectionString { get; set; } = string.Empty;

    public int ConnectionTimeout => 0;

    public string Database => "Library";

    public ConnectionState State => ConnectionState.Open;

    public IDbCommand CreateCommand() => new FakeAgentCommand(_server);

    /// <remarks>
    /// 作業查詢寫的是三段式名稱，所以產品<b>不該</b>換目錄。真的換了就當場失敗，
    /// 否則「不必為 msdb 多一次 ChangeDatabase」這件事量不出來。
    /// </remarks>
    public void ChangeDatabase(string databaseName) =>
        throw new InvalidOperationException("作業查詢不該換目錄：" + databaseName);

    public void Dispose()
    {
    }

    public void Open()
    {
    }

    public void Close()
    {
    }

    public IDbTransaction BeginTransaction() => throw new NotSupportedException();

    public IDbTransaction BeginTransaction(IsolationLevel il) => throw new NotSupportedException();
}

internal sealed class FakeAgentCommand : IDbCommand
{
    private readonly FakeAgentServer _server;
    private readonly FakeParameterCollection _parameters = new();

    internal FakeAgentCommand(FakeAgentServer server) => _server = server;

    [AllowNull] public string CommandText { get; set; } = string.Empty;

    public int CommandTimeout { get; set; }

    public CommandType CommandType { get; set; }

    public IDbConnection? Connection { get; set; }

    public IDbTransaction? Transaction { get; set; }

    public UpdateRowSource UpdatedRowSource { get; set; }

    public IDataParameterCollection Parameters => _parameters;

    public IDbDataParameter CreateParameter() => new FakeParameter();

    public void Dispose()
    {
    }

    public void Cancel()
    {
    }

    public void Prepare() => throw new NotSupportedException();

    public int ExecuteNonQuery() => throw new NotSupportedException();

    public object ExecuteScalar() => throw new NotSupportedException();

    public IDataReader ExecuteReader(CommandBehavior behavior) => ExecuteReader();

    /// <remarks>
    /// 認哪一條查詢靠各自獨有的片段，而且順序有意義：指名作業那一條是唯一帶
    /// <c>@jobId</c> 的，命令那一條是剩下唯一取 <c>s.command</c> 的，
    /// 最後才是識別那一條。照「有沒有提到 sysjobsteps」認會把三條混在一起。
    /// </remarks>
    public IDataReader ExecuteReader()
    {
        _server.Record(CommandText);

        if (_server.MsdbDenied) throw new UnreachableServerException();

        using var table = new DataTable();

        if (Mentions(SqlAgentJobSearchQueries.JobIdParameterName))
        {
            ReadJobSteps(table);
        }
        else if (Mentions("s.command"))
        {
            ReadStepCommands(table);
        }
        else
        {
            ReadJobs(table);
        }

        return table.CreateDataReader();
    }

    private bool Mentions(string fragment) => CommandText.IndexOf(fragment, StringComparison.Ordinal) >= 0;

    private void ReadJobs(DataTable table)
    {
        table.Columns.Add("job_id", typeof(Guid));
        table.Columns.Add("job_name", typeof(string));
        table.Columns.Add("job_enabled", typeof(bool));
        table.Columns.Add("step_id", typeof(int));
        table.Columns.Add("step_name", typeof(string));
        table.Columns.Add("subsystem", typeof(string));
        table.Columns.Add("database_name", typeof(string));
        table.Columns.Add("server_name", typeof(string));

        var serverName = _server.ServerName is { } name ? (object)name : DBNull.Value;

        foreach (var job in _server.Jobs)
        {
            // 沒有步驟的作業照樣回一列：產品那一條是 LEFT JOIN，而它存在的理由正是
            // 「還沒有步驟的作業仍然搜得到名稱」。
            if (job.Steps.Count == 0)
            {
                table.Rows.Add(
                    job.JobId, job.Name, job.Enabled,
                    DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, serverName);
                continue;
            }

            foreach (var step in job.Steps)
            {
                table.Rows.Add(
                    job.JobId, job.Name, job.Enabled,
                    step.StepId, step.Name, step.Subsystem,
                    step.DatabaseName.Length == 0 ? DBNull.Value : step.DatabaseName,
                    serverName);
            }
        }
    }

    private void ReadStepCommands(DataTable table)
    {
        table.Columns.Add("job_id", typeof(Guid));
        table.Columns.Add("step_id", typeof(int));
        table.Columns.Add("command", typeof(string));

        foreach (var job in _server.Jobs)
        {
            foreach (var step in job.Steps)
            {
                table.Rows.Add(job.JobId, step.StepId, step.Command);
            }
        }
    }

    private void ReadJobSteps(DataTable table)
    {
        table.Columns.Add("step_id", typeof(int));
        table.Columns.Add("step_name", typeof(string));
        table.Columns.Add("subsystem", typeof(string));
        table.Columns.Add("database_name", typeof(string));
        table.Columns.Add("command", typeof(string));

        var jobId = RequiredJobId();

        foreach (var job in _server.Jobs)
        {
            if (job.JobId != jobId) continue;

            foreach (var step in job.Steps)
            {
                table.Rows.Add(step.StepId, step.Name, step.Subsystem, step.DatabaseName, step.Command);
            }
        }
    }

    /// <remarks>
    /// 漏綁值在真實伺服器上是「必須宣告純量變數」，而那是 <c>DbException</c>，
    /// 會被降級成「這一輪沒有資料」——啟動路徑上的症狀是雙擊之後只說「取不到」。
    /// 這裡照同一個形狀失敗。
    /// </remarks>
    private Guid RequiredJobId()
    {
        if (!_parameters.Contains(SqlAgentJobSearchQueries.JobIdParameterName))
        {
            throw new UnreachableServerException();
        }

        var value = ((IDataParameter)_parameters[SqlAgentJobSearchQueries.JobIdParameterName]).Value;
        return value is Guid jobId ? jobId : throw new UnreachableServerException();
    }
}
