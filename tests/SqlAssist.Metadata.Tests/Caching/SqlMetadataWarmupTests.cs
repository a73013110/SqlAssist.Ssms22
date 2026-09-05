using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Querying;
using Xunit;

namespace SqlAssist.Metadata.Tests.Caching;

public sealed class SqlMetadataWarmupTests
{
    [Fact]
    public async Task 不開建議清單也能預載並在清除後用相同文字重新定位()
    {
        var source = new SnapshotSource();
        var catalog = Create(source);
        const string sql = "SELECT * FROM dbo.Lib_Reader";
        var lookup = SqlObjectLookup.Create(sql, sql.Length - 1)!;
        Assert.Null(lookup.FindCandidate(catalog.CachedSnapshot));

        await catalog.WarmSnapshotAsync();
        Assert.Equal(1, lookup.FindCandidate(catalog.CachedSnapshot)!.Object.ObjectId);
        await catalog.WarmSnapshotAsync();
        Assert.Equal(1, source.Attempts);

        catalog.Invalidate();
        Assert.Null(lookup.FindCandidate(catalog.CachedSnapshot));
        source.ObjectId = 2;
        await catalog.WarmSnapshotAsync();
        Assert.Equal(2, lookup.FindCandidate(catalog.CachedSnapshot)!.Object.ObjectId);
        Assert.Equal(2, source.Attempts);
    }

    [Fact]
    public async Task 慢連線不阻塞呼叫端且重複停留只載入一次()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SnapshotSource { BeforeOpen = () => Block(started, release) };
        var catalog = Create(source);
        var warming = catalog.WarmSnapshotAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(warming.IsCompleted);
            Assert.True(catalog.CachedSnapshot.IsEmpty);
            for (var index = 0; index < 32; index++)
            {
                Assert.True(catalog.WarmSnapshotAsync().IsCompletedSuccessfully);
            }

            Assert.Equal(1, source.Attempts);
        }
        finally
        {
            release.Set();
            await warming.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        Assert.False(catalog.CachedSnapshot.IsEmpty);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task 清除期間舊查詢不得回填快照或恢復失敗退避(bool fail, bool foreground)
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SnapshotSource
        {
            BeforeOpen = () => Block(started, release),
            Failure = fail ? new UnavailableException() : null,
        };
        var catalog = Create(source);
        Task warming = foreground
            ? catalog.GetSnapshotAsync(CancellationToken.None)
            : catalog.WarmSnapshotAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            catalog.Invalidate();
        }
        finally
        {
            release.Set();
            await warming.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        Assert.True(catalog.CachedSnapshot.IsEmpty);
        source.Failure = null;
        source.ObjectId = 2;
        await catalog.WarmSnapshotAsync();
        Assert.Equal(2, Assert.Single(catalog.CachedSnapshot.Objects).ObjectId);
        Assert.Equal(2, source.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 前景讀取等待預載後共用成功結果或失敗退避(bool fail)
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SnapshotSource
        {
            BeforeOpen = () => Block(started, release),
            Failure = fail ? new UnavailableException() : null,
        };
        var catalog = Create(source);
        var warming = catalog.WarmSnapshotAsync();
        var foreground = catalog.GetSnapshotAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(foreground.IsCompleted);
        }
        finally
        {
            release.Set();
            await warming.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        var snapshot = await foreground.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(fail, snapshot.IsEmpty);
        Assert.Equal(1, source.Attempts);
    }

    [Fact]
    public async Task 預載失敗會退避且清除後可以重試()
    {
        var source = new SnapshotSource { Failure = new UnavailableException() };
        var catalog = Create(source);
        await catalog.WarmSnapshotAsync();
        await catalog.WarmSnapshotAsync();
        await catalog.GetSnapshotAsync(CancellationToken.None);
        Assert.True(catalog.CachedSnapshot.IsEmpty);
        Assert.Equal(1, source.Attempts);

        source.Failure = null;
        catalog.Invalidate();
        await catalog.WarmSnapshotAsync();
        Assert.False(catalog.CachedSnapshot.IsEmpty);
        Assert.Equal(2, source.Attempts);
    }

    [Fact]
    public async Task 預載的程式錯誤交回平台觀察且釋放載入閘()
    {
        var source = new SnapshotSource { Failure = new InvalidOperationException() };
        var catalog = Create(source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.WarmSnapshotAsync());
        source.Failure = null;
        await catalog.WarmSnapshotAsync();
        Assert.False(catalog.CachedSnapshot.IsEmpty);
    }

    private static SqlMetadataCatalog Create(SnapshotSource source) =>
        new(source, TimeSpan.FromMinutes(5), failureBackoff: TimeSpan.FromMinutes(5));

    private static void Block(TaskCompletionSource<bool> started, ManualResetEventSlim release)
    {
        started.TrySetResult(true);
        if (!release.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("測試未釋放背景查詢。");
        }
    }

    /// <summary>只替代資料庫 I/O；預載、去重、失效及定位仍執行產品實作。</summary>
    private sealed class SnapshotSource : ISqlConnectionSource
    {
        private int _attempts;
        public string CacheKey => "library-server|Library";
        public string ServerCacheKey => "library-server";
        public string DatabaseName => "Library";
        public int Attempts => Volatile.Read(ref _attempts);
        public int ObjectId { get; set; } = 1;
        public Action? BeforeOpen { get; set; }
        public Exception? Failure { get; set; }

        public IDbConnection OpenConnection()
        {
            Interlocked.Increment(ref _attempts);
            var id = ObjectId;
            BeforeOpen?.Invoke();
            if (Failure is { } failure)
            {
                throw failure;
            }

            return new SnapshotConnection(id);
        }
    }

    private sealed class UnavailableException : DbException { }

    private sealed class SnapshotConnection(int objectId) : IDbConnection
    {
        [AllowNull] public string ConnectionString { get; set; } = string.Empty;
        public int ConnectionTimeout => 0;
        public string Database => "Library";
        public ConnectionState State => ConnectionState.Open;
        public IDbCommand CreateCommand() => new SnapshotCommand(objectId);
        public void Dispose() { }
        public void Open() { }
        public void Close() { }
        public void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public IDbTransaction BeginTransaction() => throw new NotSupportedException();
        public IDbTransaction BeginTransaction(IsolationLevel il) => throw new NotSupportedException();
    }

    private sealed class SnapshotCommand(int objectId) : IDbCommand
    {
        [AllowNull] public string CommandText { get; set; } = string.Empty;
        public int CommandTimeout { get; set; }
        public CommandType CommandType { get; set; }
        public IDbConnection? Connection { get; set; }
        public IDbTransaction? Transaction { get; set; }
        public UpdateRowSource UpdatedRowSource { get; set; }
        public IDataParameterCollection Parameters => throw new NotSupportedException();
        public IDbDataParameter CreateParameter() => throw new NotSupportedException();
        public void Dispose() { }
        public void Cancel() { }
        public void Prepare() => throw new NotSupportedException();
        public int ExecuteNonQuery() => throw new NotSupportedException();
        public object ExecuteScalar() => throw new NotSupportedException();
        public IDataReader ExecuteReader(CommandBehavior behavior) => ExecuteReader();

        public IDataReader ExecuteReader()
        {
            // 第一層查詢形狀與 SqlMetadataReader 一致；其餘目錄清單保留合法空結果。
            using var table = new DataTable();
            if (CommandText.Contains("FROM sys.objects", StringComparison.Ordinal))
            {
                table.Columns.Add("object_id", typeof(int));
                table.Columns.Add("schema_name", typeof(string));
                table.Columns.Add("name", typeof(string));
                table.Columns.Add("type", typeof(string));
                table.Rows.Add(objectId, "dbo", "Lib_Reader", "U");
            }
            else
            {
                table.Columns.Add("name", typeof(string));
            }

            return table.CreateDataReader();
        }
    }
}
