using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Sqlite;

/// <summary>每次操作使用獨立連線；不保留 pool，關閉後不鎖住資料庫或妨礙 VSIX 卸載。</summary>
public sealed partial class SqliteQueryMemoryRepository : IQueryMemoryRepository, ISavedQueryRepository
{
    private readonly string _connectionString;
    private string _storeId = "";

    private SqliteQueryMemoryRepository(string path, int busyTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            throw new ArgumentException("資料庫必須使用本機絕對路徑。", nameof(path));
        path = Path.GetFullPath(path);
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("WAL 資料庫不支援網路共用路徑。", nameof(path));
        if (busyTimeoutSeconds < 1 || busyTimeoutSeconds > 60) throw new ArgumentOutOfRangeException(nameof(busyTimeoutSeconds));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, ForeignKeys = true,
            DefaultTimeout = busyTimeoutSeconds, Mode = SqliteOpenMode.ReadWrite,
        }.ToString();
    }

    public static Task<SqliteQueryMemoryRepository> OpenAsync(string path, CancellationToken cancellationToken, int busyTimeoutSeconds = 5)
    {
        var repository = new SqliteQueryMemoryRepository(path, busyTimeoutSeconds);
        // Microsoft.Data.Sqlite 的 async ADO.NET 仍同步做 I/O，必須明確離開呼叫端執行緒。
        return Task.Run(() => { repository.Initialize(cancellationToken); return repository; }, cancellationToken);
    }

    private void Initialize(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString) { Mode = SqliteOpenMode.ReadWriteCreate };
        Directory.CreateDirectory(Path.GetDirectoryName(builder.DataSource) ?? throw new ArgumentException("資料庫目錄不存在。"));
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        cancellationToken.ThrowIfCancellationRequested();
        long application, version;
        using (var inspection = connection.BeginTransaction(deferred: true))
        {
            application = ScalarLong(connection, inspection, "PRAGMA application_id;");
            version = ScalarLong(connection, inspection, "PRAGMA user_version;");
            if ((application != 0 && application != SqliteSchema.ApplicationId) || version < 0 || version > SqliteSchema.Version ||
                (application == SqliteSchema.ApplicationId && version == 0))
                throw new InvalidDataException("不是支援的 Query Memory 資料庫版本。");
            // 同一讀取快照內檢查，避免另一個程序恰好完成 migration 時誤判成外來資料庫。
            if (application == 0 && (version != 0 || ScalarLong(connection, inspection,
                "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';") != 0))
                throw new InvalidDataException("資料庫已有非 Query Memory 的內容。");
            inspection.Commit();
        }
        using (var wal = Command(connection, null, "PRAGMA journal_mode=WAL;"))
            if (!string.Equals(Convert.ToString(wal.ExecuteScalar(), CultureInfo.InvariantCulture), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("無法啟用 Query Memory WAL。");
        using var transaction = connection.BeginTransaction(deferred: false);
        // 第二個程序可能在等待寫鎖時完成 migration，必須於交易內重讀。
        version = ScalarLong(connection, transaction, "PRAGMA user_version;");
        application = ScalarLong(connection, transaction, "PRAGMA application_id;");
        if (version == 0 && application == 0)
        {
            if (ScalarLong(connection, transaction, "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';") != 0)
                throw new InvalidDataException("資料庫已被其他程序初始化。");
            Execute(connection, transaction, SqliteSchema.Create);
            Execute(connection, transaction, "INSERT INTO StoreInfo VALUES($id);", ("$id", Guid.NewGuid().ToString("N")));
            Execute(connection, transaction, "PRAGMA application_id=" + SqliteSchema.ApplicationId + "; PRAGMA user_version=1;");
            version = 1;
        }
        else if (version < 1 || version > SqliteSchema.Version || application != SqliteSchema.ApplicationId)
            throw new InvalidDataException("Query Memory schema 版本不相容。");
        if (version == 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Execute(connection, transaction, SqliteSchema.Migrate1To2);
            Execute(connection, transaction, "PRAGMA user_version=2;");
        }
        using (var store = Command(connection, transaction, "SELECT StoreId FROM StoreInfo;"))
            _storeId = Convert.ToString(store.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
        if (!Guid.TryParseExact(_storeId, "N", out _)) throw new InvalidDataException("Query Memory 缺少儲存庫識別碼。");
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string Id(Guid id) => id.ToString("N");
    private static string? Id(Guid? id) => id?.ToString("N");
    private static long Ticks(DateTimeOffset time) => time.UtcDateTime.Ticks;
    private static DateTimeOffset Time(long ticks) => new(ticks, TimeSpan.Zero);
    private static string? StringOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static Guid? GuidOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.ParseExact(reader.GetString(ordinal), "N");
}
