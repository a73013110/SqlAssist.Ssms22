using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.QueryMemory;

/// <summary>程序身分三元組；PID 會被重用，必須連機器與啟動時間一起比對才算同一個程序。</summary>
[Serializable]
public sealed record QueryMemoryLeaseOwner(string MachineName, int ProcessId, DateTimeOffset ProcessStartTime);

[Serializable]
public sealed record QueryMemoryLease(string LeaseId, QueryMemoryLeaseOwner Owner, DateTimeOffset RenewedAt);

/// <summary>
/// Session 的心跳租約：租約還在就代表有程序可能還在編輯，維護不得回收該 Session 的 Recovery。
/// 同一張表另留一列固定識別碼當跨程序維護租約，不為了互斥再開第二套存活判斷。
/// </summary>
public interface IQueryMemoryLeaseRepository
{
    /// <summary>開啟或沿用本程序的租約；之後這個 repository 寫入的 Session 都標上它。</summary>
    Task<string> OpenLeaseAsync(QueryMemoryLeaseOwner owner, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>續心跳。false 表示租約列已被回收，呼叫端必須重新開啟才能繼續宣告擁有權。</summary>
    Task<bool> RenewLeaseAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>讀出心跳過期的租約，不含維護租約與自己；存活判斷由宿主做，儲存層不認得程序。</summary>
    Task<IReadOnlyList<QueryMemoryLease>> ReadExpiredLeasesAsync(DateTimeOffset expiredBefore, int limit,
        CancellationToken cancellationToken);

    /// <summary>釋放宿主已確認失效的租約：Session 解除標記後刪除租約列，Recovery 另依草稿期限回收。</summary>
    Task<int> ReleaseLeasesAsync(IReadOnlyList<string> leaseIds, DateTimeOffset expiredBefore,
        CancellationToken cancellationToken);

    /// <summary>取得跨程序維護租約；只認過期，重疊維護本來就由有界交易保證正確，不必判斷程序存活。</summary>
    Task<bool> TryAcquireMaintenanceLeaseAsync(QueryMemoryLeaseOwner owner, DateTimeOffset now,
        DateTimeOffset expiredBefore, CancellationToken cancellationToken);
}

/// <summary>
/// 決定哪些過期租約可以真的釋放。跨機器只認過期；同機還要確認三元組的程序不存在，
/// 否則會把還開著、只是心跳卡住的 SSMS 的未存檔草稿當成遺留資料回收。
/// </summary>
public sealed class QueryMemoryLeaseReaper
{
    // 啟動時間來自兩次不同的觀測，容許一秒誤差；差得更多就是 PID 被另一個程序重用了。
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    private readonly string _machineName;
    private readonly Func<int, DateTimeOffset?> _processStartTime;

    public QueryMemoryLeaseReaper(string machineName, Func<int, DateTimeOffset?> processStartTime)
    {
        if (string.IsNullOrWhiteSpace(machineName)) throw new ArgumentException("缺少本機名稱。", nameof(machineName));
        _machineName = machineName;
        _processStartTime = processStartTime ?? throw new ArgumentNullException(nameof(processStartTime));
    }

    public IReadOnlyList<string> Reclaimable(IReadOnlyList<QueryMemoryLease> expired)
    {
        if (expired == null) throw new ArgumentNullException(nameof(expired));
        var reclaimable = new List<string>();
        foreach (var lease in expired)
        {
            if (lease == null) throw new ArgumentException("租約清單含有空項目。", nameof(expired));
            if (!IsAlive(lease.Owner)) reclaimable.Add(lease.LeaseId);
        }
        return reclaimable;
    }

    private bool IsAlive(QueryMemoryLeaseOwner owner)
    {
        // 別台機器的程序查不到，也不該假設它死了；那裡的過期判斷就是全部證據。
        if (!string.Equals(owner.MachineName, _machineName, StringComparison.OrdinalIgnoreCase)) return false;
        var started = _processStartTime(owner.ProcessId);
        return started.HasValue && (started.Value - owner.ProcessStartTime).Duration() <= StartTimeTolerance;
    }
}
