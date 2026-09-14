using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.SqlMemory;

/// <summary>程序身分三元組；PID 會被重用，必須連機器與啟動時間一起比對才算同一個程序。</summary>
[Serializable]
public sealed record SqlMemoryLeaseOwner(string MachineName, int ProcessId, DateTimeOffset ProcessStartTime);

[Serializable]
public sealed record SqlMemoryLease(string LeaseId, SqlMemoryLeaseOwner Owner, DateTimeOffset RenewedAt);

/// <summary>
/// Session 的心跳租約：租約還在就代表有程序可能還在編輯，維護不得回收該 Session 的 Recovery。
/// 同一張表另留一列固定識別碼當跨程序維護租約，不為了互斥再開第二套存活判斷。
/// </summary>
/// <remarks>
/// 契約無狀態：repository 不記得「自己的」租約，租約識別碼一律由呼叫端明確傳入。
/// 同一個 repository 可以同時服務多個擁有者，心跳重開後也不會有舊值藏在實作裡。
/// </remarks>
public interface ISqlMemoryLeaseStore
{
    /// <summary>開啟或沿用 <paramref name="owner"/> 的租約並回傳識別碼；呼叫端自行保存並在提交時帶入。</summary>
    Task<string> OpenLeaseAsync(SqlMemoryLeaseOwner owner, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>續心跳。false 表示租約列已被回收，呼叫端必須重新開啟才能繼續宣告擁有權。</summary>
    Task<bool> RenewLeaseAsync(string leaseId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// 讀出心跳過期的租約，不含維護租約與 <paramref name="excludedLeaseId"/>（呼叫端自己的租約）；
    /// 存活判斷由宿主做，儲存層不認得程序。
    /// </summary>
    Task<IReadOnlyList<SqlMemoryLease>> ReadExpiredLeasesAsync(DateTimeOffset expiredBefore, int limit,
        string? excludedLeaseId, CancellationToken cancellationToken);

    /// <summary>
    /// 釋放宿主已確認失效的租約：Session 解除標記後刪除租約列，Recovery 另依草稿期限回收。
    /// 交易內重查過期，宿主判斷之後才續上的租約不刪；維護租約永遠略過。
    /// </summary>
    Task<int> ReleaseLeasesAsync(IReadOnlyList<string> leaseIds, DateTimeOffset expiredBefore,
        CancellationToken cancellationToken);

    /// <summary>取得跨程序維護租約；只認過期，重疊維護本來就由有界交易保證正確，不必判斷程序存活。</summary>
    Task<bool> TryAcquireMaintenanceLeaseAsync(SqlMemoryLeaseOwner owner, DateTimeOffset now,
        DateTimeOffset expiredBefore, CancellationToken cancellationToken);

    /// <summary>
    /// 正常卸載時交回維護租約，別的程序不必等它過期才接手共用輪次；只刪 <paramref name="owner"/> 持有的那一列。
    /// </summary>
    Task<bool> ReleaseMaintenanceLeaseAsync(SqlMemoryLeaseOwner owner, CancellationToken cancellationToken);
}

/// <summary>
/// 決定哪些過期租約可以真的釋放。跨機器只認過期；同機還要確認三元組的程序不存在，
/// 否則會把還開著、只是心跳卡住的 SSMS 的未存檔草稿當成遺留資料回收。
/// </summary>
public sealed class SqlMemoryLeaseReaper
{
    // 啟動時間來自兩次不同的觀測，容許一秒誤差；差得更多就是 PID 被另一個程序重用了。
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    private readonly string _machineName;
    private readonly Func<SqlMemoryLeaseOwner, bool> _isOwnerRunning;

    /// <param name="isOwnerRunning">
    /// 這個擁有者的程序是否還在。判斷不出來時必須回報 true：寧可留下遺留租約，
    /// 也不要刪掉使用者正在編輯的未存檔內容。比對啟動時間請用 <see cref="IsSameProcess"/>。
    /// </param>
    public SqlMemoryLeaseReaper(string machineName, Func<SqlMemoryLeaseOwner, bool> isOwnerRunning)
    {
        if (string.IsNullOrWhiteSpace(machineName)) throw new ArgumentException("缺少本機名稱。", nameof(machineName));
        _machineName = machineName;
        _isOwnerRunning = isOwnerRunning ?? throw new ArgumentNullException(nameof(isOwnerRunning));
    }

    /// <summary>PID 會被重用；啟動時間對不上就是另一個程序，原本那個已經不在了。</summary>
    public static bool IsSameProcess(SqlMemoryLeaseOwner owner, DateTimeOffset startTime)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        return (startTime - owner.ProcessStartTime).Duration() <= StartTimeTolerance;
    }

    public IReadOnlyList<string> Reclaimable(IReadOnlyList<SqlMemoryLease> expired)
    {
        if (expired == null) throw new ArgumentNullException(nameof(expired));
        var reclaimable = new List<string>();
        foreach (var lease in expired)
        {
            if (lease == null) throw new ArgumentException("租約清單含有空項目。", nameof(expired));
            // 別台機器的程序查不到，也不該假設它還活著；那裡的心跳過期就是全部證據。
            var running = string.Equals(lease.Owner.MachineName, _machineName, StringComparison.OrdinalIgnoreCase)
                && _isOwnerRunning(lease.Owner);
            if (!running) reclaimable.Add(lease.LeaseId);
        }
        return reclaimable;
    }
}
