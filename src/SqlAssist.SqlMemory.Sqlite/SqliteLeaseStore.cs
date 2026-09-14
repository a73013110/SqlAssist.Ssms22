using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using static SqlAssist.SqlMemory.Sqlite.SqliteDatabase;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>Session 心跳租約與跨程序維護租約。沒有「自己的租約」欄位：識別碼一律由呼叫端傳入。</summary>
internal sealed class SqliteLeaseStore
{
    private const string LeaseColumns = "SELECT LeaseId,MachineName,ProcessId,ProcessStartTime,RenewedAt FROM Leases";

    private readonly SqliteDatabase _database;

    public SqliteLeaseStore(SqliteDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

    public string OpenLease(SqlMemoryLeaseOwner owner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (string.IsNullOrWhiteSpace(owner.MachineName)) throw new ArgumentException("租約缺少機器名稱。", nameof(owner));
        using var connection = _database.Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        cancellationToken.ThrowIfCancellationRequested();
        // 同一個程序重開儲存要沿用原本那一列，否則既有 Session 會留在沒人續心跳的租約上。
        string? lease;
        using (var existing = Command(connection, transaction, "SELECT LeaseId FROM Leases" +
            " WHERE MachineName=$machine AND ProcessId=$process AND ProcessStartTime=$started AND LeaseId<>$reserved;",
            OwnerParameters(owner)))
            lease = existing.ExecuteScalar() as string;
        if (lease == null)
        {
            lease = Guid.NewGuid().ToString("N");
            Execute(connection, transaction, "INSERT INTO Leases VALUES($id,$machine,$process,$started,$now);",
                Append(OwnerParameters(owner), ("$id", lease), ("$now", Ticks(now))));
        }
        else
        {
            Execute(connection, transaction, "UPDATE Leases SET RenewedAt=$now WHERE LeaseId=$id;",
                ("$id", lease), ("$now", Ticks(now)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return lease;
    }

    public bool RenewLease(string leaseId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(leaseId)) throw new ArgumentException("缺少租約識別碼。", nameof(leaseId));
        // 維護租約有自己的取得與交回流程；拿它當 Session 租約續約會讓過期判斷失真。
        if (leaseId == SqliteSchema.MaintenanceLeaseId) throw new ArgumentException("維護租約不能當成 Session 租約續約。", nameof(leaseId));
        using var connection = _database.Connect();
        cancellationToken.ThrowIfCancellationRequested();
        Execute(connection, null, "UPDATE Leases SET RenewedAt=$now WHERE LeaseId=$id;",
            ("$id", leaseId), ("$now", Ticks(now)));
        return Changes(connection, null) == 1;
    }

    public IReadOnlyList<SqlMemoryLease> ReadExpiredLeases(DateTimeOffset expiredBefore, int limit, string? excludedLeaseId,
        CancellationToken cancellationToken)
    {
        if (limit < 1 || limit > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = _database.Connect();
        cancellationToken.ThrowIfCancellationRequested();
        // IX_Leases_Renewed 讓過期租約只掃描最舊的前幾列，不隨歷史租約數量成長。
        using var command = Command(connection, null, LeaseColumns +
            " WHERE RenewedAt<$before AND LeaseId<>$reserved AND ($excluded IS NULL OR LeaseId<>$excluded)" +
            " ORDER BY RenewedAt LIMIT $limit;",
            ("$before", Ticks(expiredBefore)), ("$reserved", SqliteSchema.MaintenanceLeaseId),
            ("$excluded", excludedLeaseId), ("$limit", limit));
        using var reader = command.ExecuteReader();
        var leases = new List<SqlMemoryLease>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            leases.Add(ReadLease(reader));
        }
        return leases;
    }

    public int ReleaseLeases(IReadOnlyList<string> leaseIds, DateTimeOffset expiredBefore, CancellationToken cancellationToken)
    {
        if (leaseIds == null) throw new ArgumentNullException(nameof(leaseIds));
        if (leaseIds.Count > 500) throw new ArgumentOutOfRangeException(nameof(leaseIds));
        using var connection = _database.Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        var released = 0;
        foreach (var leaseId in leaseIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (leaseId == null) throw new ArgumentException("租約清單含有空項目。", nameof(leaseIds));
            if (leaseId == SqliteSchema.MaintenanceLeaseId) continue;
            // IMMEDIATE 交易內重查過期；宿主判斷存活到這裡刪除之間，對方可能已經回來續約。
            using (var still = Command(connection, transaction,
                "SELECT 1 FROM Leases WHERE LeaseId=$id AND RenewedAt<$before;",
                ("$id", leaseId), ("$before", Ticks(expiredBefore))))
                if (still.ExecuteScalar() == null) continue;
            // 外鍵擋住先刪租約；Session 解除標記之後，它的 Recovery 才改依草稿期限回收。
            Execute(connection, transaction, "UPDATE Sessions SET LeaseId=NULL WHERE LeaseId=$id;", ("$id", leaseId));
            Execute(connection, transaction, "DELETE FROM Leases WHERE LeaseId=$id;", ("$id", leaseId));
            released++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return released;
    }

    public bool TryAcquireMaintenanceLease(SqlMemoryLeaseOwner owner, DateTimeOffset now,
        DateTimeOffset expiredBefore, CancellationToken cancellationToken)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        using var connection = _database.Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        cancellationToken.ThrowIfCancellationRequested();
        SqlMemoryLease? held;
        using (var command = Command(connection, transaction, LeaseColumns + " WHERE LeaseId=$reserved;",
            ("$reserved", SqliteSchema.MaintenanceLeaseId)))
        using (var reader = command.ExecuteReader())
            held = reader.Read() ? ReadLease(reader) : null;
        // 只認過期：維護重疊本來就由有界交易保證正確，不必也無法在這裡判斷對方死活。
        if (held != null && held.Owner != owner && held.RenewedAt >= expiredBefore) return false;
        Execute(connection, transaction, held == null
            ? "INSERT INTO Leases VALUES($reserved,$machine,$process,$started,$now);"
            : "UPDATE Leases SET MachineName=$machine,ProcessId=$process,ProcessStartTime=$started,RenewedAt=$now WHERE LeaseId=$reserved;",
            Append(OwnerParameters(owner), ("$now", Ticks(now))));
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return true;
    }

    public bool ReleaseMaintenanceLease(SqlMemoryLeaseOwner owner, CancellationToken cancellationToken)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        using var connection = _database.Connect();
        cancellationToken.ThrowIfCancellationRequested();
        // 單一條件刪除本身是原子的；別人已經接手（三元組不同）就什麼都不動，共用狀態也不跟著清。
        Execute(connection, null, "DELETE FROM Leases WHERE LeaseId=$reserved AND MachineName=$machine" +
            " AND ProcessId=$process AND ProcessStartTime=$started;", OwnerParameters(owner));
        return Changes(connection, null) == 1;
    }

    /// <summary>維護批次在自己的交易內確認維護租約仍屬這個擁有者；參數名稱與這裡的 SQL 一致。</summary>
    public static (string Name, object? Value)[] OwnerParameters(SqlMemoryLeaseOwner owner) => new (string, object?)[]
    {
        ("$machine", owner.MachineName), ("$process", owner.ProcessId),
        ("$started", Ticks(owner.ProcessStartTime)), ("$reserved", SqliteSchema.MaintenanceLeaseId),
    };

    private static SqlMemoryLease ReadLease(SqliteDataReader reader) => new(reader.GetString(0),
        new SqlMemoryLeaseOwner(reader.GetString(1), reader.GetInt32(2), Time(reader.GetInt64(3))),
        Time(reader.GetInt64(4)));

    private static (string Name, object? Value)[] Append((string Name, object? Value)[] parameters,
        params (string Name, object? Value)[] extra)
    {
        var combined = new (string Name, object? Value)[parameters.Length + extra.Length];
        parameters.CopyTo(combined, 0);
        extra.CopyTo(combined, parameters.Length);
        return combined;
    }
}
