using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.SqlMemory;

/// <summary>
/// 本程序的 Session 心跳。只決定「該續了沒」與「續不到要重開」，計時器由宿主提供。
/// </summary>
/// <remarks>
/// 心跳間隔必須遠短於租約期限：兩者相等時，一次排程延遲就會讓別的程序看到過期租約。
/// 續約回報 false 是租約列已被回收的唯一訊號，這時重新開一個——舊租約上的 Session
/// 已經回到無人擁有，追認不回來，只能讓之後寫入的 Session 掛在新租約上。
/// </remarks>
public sealed class SqlMemoryLeaseHeartbeat
{
    private readonly ISqlMemoryLeaseStore _leases;
    private readonly SqlMemoryLeaseOwner _owner;
    private DateTimeOffset? _lastBeatAt;
    private string? _leaseId;

    public SqlMemoryLeaseHeartbeat(ISqlMemoryLeaseStore leases, SqlMemoryLeaseOwner owner, TimeSpan interval)
    {
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        Interval = interval;
    }

    public TimeSpan Interval { get; }

    /// <summary>
    /// 目前持有的租約；還沒開或剛被回收時為 null。背景 writer 會在另一條執行緒讀取，
    /// 讀到剛被回收的舊值也無妨：提交交易會確認租約列仍存在。
    /// </summary>
    public string? LeaseId
    {
        get => Volatile.Read(ref _leaseId);
        private set => Volatile.Write(ref _leaseId, value);
    }

    /// <summary>租約被回收過幾次；用來回報「未存檔草稿的擁有權曾經中斷」。</summary>
    public int ReopenCount { get; private set; }

    public bool IsDue(DateTimeOffset now) => !_lastBeatAt.HasValue || now - _lastBeatAt.Value >= Interval;

    /// <summary>開啟或續租；例外交給宿主，不假裝還持有租約。</summary>
    public async Task<string> BeatAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (LeaseId is { } current)
        {
            if (await _leases.RenewLeaseAsync(current, now, cancellationToken).ConfigureAwait(false))
            {
                _lastBeatAt = now;
                return current;
            }

            // 先放掉再開：中途失敗時寧可回報「沒有租約」，也不要讓維護以為草稿還被保護著。
            LeaseId = null;
            ReopenCount++;
        }

        var opened = await _leases.OpenLeaseAsync(_owner, now, cancellationToken).ConfigureAwait(false);
        LeaseId = opened ?? throw new InvalidOperationException("儲存層沒有回傳租約識別碼。");
        _lastBeatAt = now;
        return opened;
    }
}
