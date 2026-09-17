using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using static SqlAssist.SqlMemory.Sqlite.SqliteDatabase;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>維護：用量、實體整理、共用輪次狀態，以及一個 IMMEDIATE 交易內的有界清理批次。</summary>
internal sealed class SqliteMaintenanceStore
{
    private readonly SqliteDatabase _database;

    public SqliteMaintenanceStore(SqliteDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

    public SqlMemoryUsage ReadUsage(CancellationToken cancellationToken)
    {
        using var connection = _database.Connect();
        cancellationToken.ThrowIfCancellationRequested();
        return ReadUsage(connection, null);
    }

    private SqlMemoryUsage ReadUsage(SqliteConnection connection, SqliteTransaction? transaction) =>
        ReadUsage(_database, connection, transaction);

    /// <summary>內容計量讀交易內的值；檔案大小只是觀測，與交易快照無關。</summary>
    internal static SqlMemoryUsage ReadUsage(SqliteDatabase database, SqliteConnection connection, SqliteTransaction? transaction)
    {
        var bytes = ScalarLong(connection, transaction, "SELECT ContentBytes FROM StorageUsage WHERE Id=1;");
        var path = database.FilePath;
        return new SqlMemoryUsage(bytes, FileLength(path), FileLength(path + "-wal"));
    }

    private static long FileLength(string path)
    {
        // WAL 可在另一個連線關閉時消失；檔案大小僅是觀測，不是 SQLite 交易快照。
        try { return new FileInfo(path).Length; }
        catch (FileNotFoundException) { return 0; }
    }

    public SqlMemoryCheckpointResult Checkpoint(CancellationToken cancellationToken)
    {
        using var connection = _database.Connect();
        cancellationToken.ThrowIfCancellationRequested();
        // TRUNCATE 要求所有讀取者都在最新快照；還有人讀舊快照就回報 busy，不中斷他們也不改資料。
        bool truncated;
        using (var command = Command(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);"))
        using (var reader = command.ExecuteReader())
            truncated = reader.Read() && reader.GetInt64(0) == 0;
        return new SqlMemoryCheckpointResult(truncated, ReadUsage(connection, null));
    }

    /// <remarks>VACUUM 本身不可中斷，取消只在開始前生效；期間其他連線的寫入等到 busy timeout 就回報忙碌。</remarks>
    public SqlMemoryUsage Compact(CancellationToken cancellationToken)
    {
        using var connection = _database.Connect();
        cancellationToken.ThrowIfCancellationRequested();
        // VACUUM 不能在交易內；重建期間需要與資料庫等量的暫存空間，因此不排進背景維護。
        Execute(connection, null, "VACUUM;");
        // 重建結果先進 WAL，不接著 checkpoint 主檔案就不會縮小，使用者會看到「整理完卻沒變小」。
        Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
        return ReadUsage(connection, null);
    }

    public SqlMemoryMaintenanceState? ReadMaintenanceState(CancellationToken cancellationToken)
    {
        using var connection = _database.Connect();
        cancellationToken.ThrowIfCancellationRequested();
        using var command = Command(connection, null, @"SELECT Version,PlanFingerprint,RoundStartedAt,Level,ReclaimsRecovery,
Scan,RoundsSinceFullScan,Cursor,RequiresAnotherPass,CapacityStatus FROM MaintenanceState WHERE Id=1;");
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var round = new SqlMemoryMaintenanceRound(reader.GetString(1), Time(reader.GetInt64(2)), reader.GetInt32(3),
            reader.GetInt64(4) == 1, (SqlMemoryMaintenanceScan)reader.GetInt32(5), reader.GetInt32(6));
        return new SqlMemoryMaintenanceState(reader.GetInt64(0), round, StringOrNull(reader, 7), reader.GetInt64(8) == 1,
            (SqlMemoryCapacityStatus)reader.GetInt32(9));
    }

    public SqlMemoryMaintenanceResult Maintain(SqlMemoryMaintenanceRequest request, CancellationToken token)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        token.ThrowIfCancellationRequested();
        var stages = SqliteMaintenanceStages.For(request.Scan);
        var cursor = new SqliteMaintenanceCursor(_database.StoreId, request, stages.Length);
        using var connection = _database.Connect();
        // 租約／狀態版本確認、候選、保護根重查、刪除與狀態寫回共用 IMMEDIATE 交易，
        // Favorite 更新或另一個維護者都不可能插進檢查與刪除之間。
        using var transaction = connection.BeginTransaction(deferred: false);
        token.ThrowIfCancellationRequested();
        if (request.Claim != null) VerifyClaim(connection, transaction, request.Claim);
        var batch = new SqliteMaintenanceBatch(connection, transaction, request.Policy, token);
        while (!cursor.Completed && batch.Examined < request.CandidateLimit)
        {
            if (!batch.Advance(stages[cursor.Stage], cursor, request.CandidateLimit - batch.Examined)) cursor.NextStage();
        }
        batch.CollectReleasedReferences();
        cursor.MadeProgress |= batch.Deleted > 0;
        var completed = cursor.Completed;
        var usage = ReadUsage(connection, transaction);
        var capacity = !request.Policy.MaxContentBytes.HasValue || usage.ContentBytes <= request.Policy.MaxContentBytes.Value
            ? SqlMemoryCapacityStatus.WithinLimit
            : !completed || cursor.MadeProgress ? SqlMemoryCapacityStatus.MoreWorkRequired
            : SqlMemoryCapacityStatus.CannotReclaimWithinPolicy;
        var result = new SqlMemoryMaintenanceResult(batch.Examined, batch.Deleted, completed ? null : cursor.Encode(),
            completed && cursor.MadeProgress, usage, capacity);
        if (request.Claim != null) SaveState(connection, transaction, request.Claim, result);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return result;
    }

    private static void VerifyClaim(SqliteConnection connection, SqliteTransaction transaction, SqlMemoryMaintenanceClaim claim)
    {
        // 只認三元組相同：租約過期後被別的程序接手，列上的擁有者就換了，晚到的這一批不得寫回。
        using (var lease = Command(connection, transaction, "SELECT 1 FROM Leases WHERE LeaseId=$reserved" +
            " AND MachineName=$machine AND ProcessId=$process AND ProcessStartTime=$started;", SqliteLeaseStore.OwnerParameters(claim.Owner)))
            if (lease.ExecuteScalar() == null) throw Conflict("維護租約已不屬於這個程序；這一批不寫入。");
        var version = ScalarLong(connection, transaction, "SELECT coalesce((SELECT Version FROM MaintenanceState WHERE Id=1),0);");
        if (version != claim.ExpectedVersion) throw Conflict("維護狀態已被其他維護者推進；重讀後再接續。");
    }

    private static void SaveState(SqliteConnection connection, SqliteTransaction transaction, SqlMemoryMaintenanceClaim claim,
        SqlMemoryMaintenanceResult result)
    {
        var round = claim.Round;
        Execute(connection, transaction, @"INSERT INTO MaintenanceState VALUES(1,$version,$plan,$started,$level,$reclaims,$scan,$since,$cursor,$another,$capacity)
ON CONFLICT(Id) DO UPDATE SET Version=excluded.Version, PlanFingerprint=excluded.PlanFingerprint,
RoundStartedAt=excluded.RoundStartedAt, Level=excluded.Level, ReclaimsRecovery=excluded.ReclaimsRecovery,
Scan=excluded.Scan, RoundsSinceFullScan=excluded.RoundsSinceFullScan, Cursor=excluded.Cursor,
RequiresAnotherPass=excluded.RequiresAnotherPass, CapacityStatus=excluded.CapacityStatus;",
            ("$version", claim.ExpectedVersion + 1), ("$plan", round.PlanFingerprint), ("$started", Ticks(round.StartedAt)),
            ("$level", round.Level), ("$reclaims", round.ReclaimsRecovery), ("$scan", (int)round.Scan),
            ("$since", round.RoundsSinceFullScan), ("$cursor", result.Cursor), ("$another", result.RequiresAnotherPass),
            ("$capacity", (int)result.CapacityStatus));
    }

    private static SqlMemoryStorageException Conflict(string message) => new(SqlMemoryStorageErrorKind.Conflict, message);
}
