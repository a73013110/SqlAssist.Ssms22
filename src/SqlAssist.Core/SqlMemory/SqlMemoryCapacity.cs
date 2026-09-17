using System;

namespace SqlAssist.Core.SqlMemory;

public enum SqlMemoryUsageSeverity { Normal, Warning, Critical }

/// <summary>容量比例與分級的唯一規則；用量頁、工具列警示點與通知共用。</summary>
public static class SqlMemoryCapacity
{
    public const double WarningRatio = 0.7;
    public const double CriticalRatio = 0.9;

    /// <summary>通知送出後要降到這個比例以下才重新武裝；在門檻上下擺動時不會一直跳通知。</summary>
    public const double RearmRatio = 0.8;

    /// <returns>null 表示沒有上限，沒有比例可言。</returns>
    public static double? Ratio(long used, long? limit) =>
        limit is { } value ? value <= 0 ? used > 0 ? double.PositiveInfinity : 0 : (double)Math.Max(0, used) / value : null;

    public static SqlMemoryUsageSeverity Severity(double? ratio) =>
        ratio >= CriticalRatio ? SqlMemoryUsageSeverity.Critical
        : ratio >= WarningRatio ? SqlMemoryUsageSeverity.Warning
        : SqlMemoryUsageSeverity.Normal;

    /// <summary>容量分級另外看維護結論：規則下清不下來就算還沒到 90% 也是需要使用者處理的狀態。</summary>
    public static SqlMemoryUsageSeverity Severity(SqlMemoryUsage usage, long? limit, SqlMemoryCapacityStatus? status)
    {
        if (usage == null) throw new ArgumentNullException(nameof(usage));
        return status == SqlMemoryCapacityStatus.CannotReclaimWithinPolicy
            ? SqlMemoryUsageSeverity.Critical
            : Severity(Ratio(usage.ContentBytes, limit));
    }
}

/// <summary>
/// 追蹤最近一次觀測到的容量分級，決定工具列警示與「接近上限」通知；執行緒安全。
/// </summary>
/// <remarks>
/// 通知只在進入 Critical 時送一次，比例降到 <see cref="SqlMemoryCapacity.RearmRatio"/> 以下才重新武裝，
/// 避免維護每刪一批就在門檻上下來回觸發。
/// </remarks>
public sealed class SqlMemoryCapacityMonitor
{
    private readonly object _sync = new();
    private bool _armed = true;

    public SqlMemoryUsageSeverity Severity { get; private set; }

    public double? Ratio { get; private set; }

    /// <returns>分級是否改變，以及這次是否應該送出通知。</returns>
    public (bool Changed, bool Notify) Observe(SqlMemoryUsage usage, long? limit, SqlMemoryCapacityStatus? status)
    {
        if (usage == null) throw new ArgumentNullException(nameof(usage));
        var ratio = SqlMemoryCapacity.Ratio(usage.ContentBytes, limit);
        var severity = SqlMemoryCapacity.Severity(usage, limit, status);
        lock (_sync)
        {
            var changed = severity != Severity;
            Severity = severity;
            Ratio = ratio;
            var notify = false;
            if (severity == SqlMemoryUsageSeverity.Critical)
            {
                notify = _armed;
                _armed = false;
            }
            else if (!(ratio >= SqlMemoryCapacity.RearmRatio)) _armed = true;
            return (changed, notify);
        }
    }

    /// <summary>換一份儲存或停用：舊資料庫的分級不能留在新畫面上。</summary>
    public bool Reset()
    {
        lock (_sync)
        {
            var changed = Severity != SqlMemoryUsageSeverity.Normal;
            Severity = SqlMemoryUsageSeverity.Normal;
            Ratio = null;
            _armed = true;
            return changed;
        }
    }
}

/// <summary>容量分級改變或需要通知；<see cref="Notify"/> 只在剛進入 Critical 且通知已武裝時為 true。</summary>
public sealed class SqlMemoryCapacityChangedEventArgs : EventArgs
{
    public SqlMemoryCapacityChangedEventArgs(SqlMemoryUsageSeverity severity, double? ratio, bool notify)
    {
        Severity = severity;
        Ratio = ratio;
        Notify = notify;
    }

    public SqlMemoryUsageSeverity Severity { get; }
    public double? Ratio { get; }
    public bool Notify { get; }
}
