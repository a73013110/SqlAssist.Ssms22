using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>一個 SQL Memory 資料庫檔案：路徑檢查、pragma、schema 身分檢查與指令輔助。</summary>
/// <remarks>
/// 每次操作使用獨立連線；不保留 pool，關閉後不鎖住資料庫或妨礙 VSIX 卸載。
/// 開啟後沒有可變狀態，各聚合的 store 可由多條執行緒同時使用；並行交給 SQLite WAL 與交易。
/// 只提供同步方法，由隔離 AppDomain 的 worker 直接呼叫；非同步與排背景只在隔離邊界做一次，
/// 不再「Task.Run 包 I/O、worker 又同步等待」而一個操作占兩條執行緒。
/// </remarks>
internal sealed class SqliteDatabase
{
    private readonly string _connectionString;

    private SqliteDatabase(string path, int busyTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            throw new ArgumentException("資料庫必須使用本機絕對路徑。", nameof(path));
        path = Path.GetFullPath(path);
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("WAL 資料庫不支援網路共用路徑。", nameof(path));
        if (busyTimeoutSeconds < 1 || busyTimeoutSeconds > 60) throw new ArgumentOutOfRangeException(nameof(busyTimeoutSeconds));
        FilePath = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, ForeignKeys = true,
            DefaultTimeout = busyTimeoutSeconds, Mode = SqliteOpenMode.ReadWrite,
        }.ToString();
    }

    public string FilePath { get; }

    /// <summary>建立資料庫時產生的識別碼；游標綁它，換一個資料庫檔案的舊游標必須被拒絕。</summary>
    public string StoreId { get; private set; } = "";

    public static SqliteDatabase Open(string path, CancellationToken cancellationToken, int busyTimeoutSeconds = 5)
    {
        var database = new SqliteDatabase(path, busyTimeoutSeconds);
        database.Initialize(cancellationToken);
        return database;
    }

    public SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
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
            if ((application != 0 && application != SqliteSchema.ApplicationId) || (application == SqliteSchema.ApplicationId && version != SqliteSchema.Version))
                throw Incompatible("不是支援的 SQL Memory 資料庫版本。");
            // 同一讀取快照內檢查，避免另一個程序恰好完成初始化時誤判成外來資料庫。
            if (application == 0 && (version != 0 || ScalarLong(connection, inspection,
                "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';") != 0))
                throw Incompatible("資料庫已有非 SQL Memory 的內容。");
            inspection.Commit();
        }
        using (var wal = Command(connection, null, "PRAGMA journal_mode=WAL;"))
            if (!string.Equals(Convert.ToString(wal.ExecuteScalar(), CultureInfo.InvariantCulture), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("無法啟用 SQL Memory WAL。");
        using var transaction = connection.BeginTransaction(deferred: false);
        // 第二個程序可能在等待寫鎖時完成初始化，必須於交易內重讀。
        version = ScalarLong(connection, transaction, "PRAGMA user_version;");
        application = ScalarLong(connection, transaction, "PRAGMA application_id;");
        if (version == 0 && application == 0)
        {
            if (ScalarLong(connection, transaction, "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';") != 0)
                throw Incompatible("資料庫已被其他程序初始化。");
            Execute(connection, transaction, SqliteSchema.Create);
            Execute(connection, transaction, "INSERT INTO StoreInfo VALUES($id);", ("$id", Guid.NewGuid().ToString("N")));
            Execute(connection, transaction, "PRAGMA application_id=" + SqliteSchema.ApplicationId + "; PRAGMA user_version=" + SqliteSchema.Version + ";");
        }
        else if (version != SqliteSchema.Version || application != SqliteSchema.ApplicationId)
            throw Incompatible("SQL Memory schema 版本不相容；請使用新的開發測試資料庫。");
        string storeId;
        using (var store = Command(connection, transaction, "SELECT StoreId FROM StoreInfo;"))
            storeId = Convert.ToString(store.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
        if (!Guid.TryParseExact(storeId, "N", out _)) throw new InvalidDataException("SQL Memory 缺少儲存庫識別碼。");
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        StoreId = storeId;
    }

    private static SqlMemoryStorageException Incompatible(string message) =>
        new(SqlMemoryStorageErrorKind.Incompatible, message);

    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    public static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    public static long ScalarLong(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>上一句 DML 影響的列數；必須在同一個連線上緊接著呼叫。</summary>
    public static int Changes(SqliteConnection connection, SqliteTransaction? transaction) =>
        (int)ScalarLong(connection, transaction, "SELECT changes();");

    public static string Id(Guid id) => id.ToString("N");
    public static string? Id(Guid? id) => id?.ToString("N");
    public static long Ticks(DateTimeOffset time) => time.UtcDateTime.Ticks;
    public static DateTimeOffset Time(long ticks) => new(ticks, TimeSpan.Zero);
    public static string? StringOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    public static Guid? GuidOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.ParseExact(reader.GetString(ordinal), "N");
}
