using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Sqlite;

internal sealed partial class SqliteQueryMemoryRepository
{
    private const string LeaseColumns = "SELECT LeaseId,MachineName,ProcessId,ProcessStartTime,RenewedAt FROM Leases";

    public string OpenLease(QueryMemoryLeaseOwner owner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (string.IsNullOrWhiteSpace(owner.MachineName)) throw new ArgumentException("租約缺少機器名稱。", nameof(owner));
        using var connection = Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        cancellationToken.ThrowIfCancellationRequested();
        // 同一個程序重開 repository 要沿用原本那一列，否則既有 Session 會留在沒人續心跳的租約上。
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
        // 提交後才記住：忙碌或取消回滾時，欄位不能指向一個 Leases 裡不存在的識別碼。
        LeaseId = lease;
        return lease;
    }

    public bool RenewLease(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lease = LeaseId ?? throw new InvalidOperationException("尚未開啟 Query Memory 租約。");
        using var connection = Connect();
        cancellationToken.ThrowIfCancellationRequested();
        Execute(connection, null, "UPDATE Leases SET RenewedAt=$now WHERE LeaseId=$id;",
            ("$id", lease), ("$now", Ticks(now)));
        return ScalarLong(connection, null, "SELECT changes();") == 1;
    }

    public IReadOnlyList<QueryMemoryLease> ReadExpiredLeases(DateTimeOffset expiredBefore, int limit,
        CancellationToken cancellationToken)
    {
        if (limit < 1 || limit > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var mine = LeaseId;
        using var connection = Connect();
        cancellationToken.ThrowIfCancellationRequested();
        // IX_Leases_Renewed 讓過期租約只掃描最舊的前幾列，不隨歷史租約數量成長。
        using var command = Command(connection, null, LeaseColumns +
            " WHERE RenewedAt<$before AND LeaseId<>$reserved AND ($mine IS NULL OR LeaseId<>$mine)" +
            " ORDER BY RenewedAt LIMIT $limit;",
            ("$before", Ticks(expiredBefore)), ("$reserved", SqliteSchema.MaintenanceLeaseId),
            ("$mine", mine), ("$limit", limit));
        using var reader = command.ExecuteReader();
        var leases = new List<QueryMemoryLease>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            leases.Add(ReadLease(reader));
        }
        return leases;
    }

    public int ReleaseLeases(IReadOnlyList<string> leaseIds, DateTimeOffset expiredBefore,
        CancellationToken cancellationToken)
    {
        if (leaseIds == null) throw new ArgumentNullException(nameof(leaseIds));
        if (leaseIds.Count > 500) throw new ArgumentOutOfRangeException(nameof(leaseIds));
        var mine = LeaseId;
        using var connection = Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        var released = 0;
        foreach (var leaseId in leaseIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (leaseId == null) throw new ArgumentException("租約清單含有空項目。", nameof(leaseIds));
            if (leaseId == SqliteSchema.MaintenanceLeaseId || leaseId == mine) continue;
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

    public bool TryAcquireMaintenanceLease(QueryMemoryLeaseOwner owner, DateTimeOffset now,
        DateTimeOffset expiredBefore, CancellationToken cancellationToken)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        using var connection = Connect();
        using var transaction = connection.BeginTransaction(deferred: false);
        cancellationToken.ThrowIfCancellationRequested();
        QueryMemoryLease? held;
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

    public bool ReleaseMaintenanceLease(QueryMemoryLeaseOwner owner, CancellationToken cancellationToken)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        using var connection = Connect();
        cancellationToken.ThrowIfCancellationRequested();
        // 單一條件刪除本身是原子的；別人已經接手（三元組不同）就什麼都不動，共用狀態也不跟著清。
        Execute(connection, null, "DELETE FROM Leases WHERE LeaseId=$reserved AND MachineName=$machine" +
            " AND ProcessId=$process AND ProcessStartTime=$started;", OwnerParameters(owner));
        return ScalarLong(connection, null, "SELECT changes();") == 1;
    }

    private static QueryMemoryLease ReadLease(SqliteDataReader reader) => new(reader.GetString(0),
        new QueryMemoryLeaseOwner(reader.GetString(1), reader.GetInt32(2), Time(reader.GetInt64(3))),
        Time(reader.GetInt64(4)));

    private static (string Name, object? Value)[] OwnerParameters(QueryMemoryLeaseOwner owner) => new (string, object?)[]
    {
        ("$machine", owner.MachineName), ("$process", owner.ProcessId),
        ("$started", Ticks(owner.ProcessStartTime)), ("$reserved", SqliteSchema.MaintenanceLeaseId),
    };

    private static (string Name, object? Value)[] Append((string Name, object? Value)[] parameters,
        params (string Name, object? Value)[] extra)
    {
        var combined = new (string Name, object? Value)[parameters.Length + extra.Length];
        parameters.CopyTo(combined, 0);
        extra.CopyTo(combined, parameters.Length);
        return combined;
    }
}
