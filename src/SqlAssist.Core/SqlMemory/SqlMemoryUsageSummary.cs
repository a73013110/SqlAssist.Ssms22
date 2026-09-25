using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SqlAssist.Core.Diagnostics;

namespace SqlAssist.Core.SqlMemory;

/// <summary>本程序的維護排程觀測；共用輪次的級數與結論另在 <see cref="SqlMemoryMaintenanceState"/>。</summary>
/// <param name="NextDueAt">下一次排程維護的時間；儲存沒有開啟時為 null。</param>
/// <param name="LastMaintainedAt">本程序最後一次真的跑了維護批次的時間。</param>
/// <param name="HeartbeatActive">本程序持有 Session 心跳租約；沒有時不能清除回復內容。</param>
public sealed record SqlMemoryMaintenanceOverview(DateTimeOffset? NextDueAt, DateTimeOffset? LastMaintainedAt, int Level,
    bool PendingWork, bool HeartbeatActive);

/// <summary>用量頁一次讀回的所有原始資料；轉成畫面文字由 <see cref="SqlMemoryUsageSummary"/> 負責。</summary>
public sealed record SqlMemoryUsageSnapshot(SqlMemoryUsageReport Report, SqlMemoryMaintenanceState? State,
    SqlRetentionSettings Plan, SqlMemoryMaintenanceOverview Maintenance, IReadOnlyList<SqlMemoryActivity> Activities);

/// <param name="Ratio">null 表示沒有上限，畫面只顯示數字、不畫進度條。</param>
public sealed record SqlMemoryGauge(string Label, string Value, double? Ratio, SqlMemoryUsageSeverity Severity, string Detail);

public sealed record SqlMemoryStat(string Label, string Value, string Detail);

/// <param name="Ratio">相對於第一名的比例，0～1。</param>
public sealed record SqlMemoryShareBar(string Name, string Value, double Ratio);

public sealed record SqlMemoryActivityLine(string Title, string Detail, string Time, bool Failed);

/// <summary>
/// 用量頁的畫面模型：比例、分級、健康狀態與所有文案都在這裡決定，WPF 只負責排版與動畫。
/// </summary>
public sealed class SqlMemoryUsageSummary
{
    /// <summary>可回收空間低於這個值不建議壓縮；VACUUM 重建整個檔案，換回幾百 KB 不值得。</summary>
    public const long CompactWorthwhileBytes = 1024L * 1024;

    private SqlMemoryUsageSummary(SqlMemoryGauge capacity, string disk, bool compactRecommended, SqlMemoryUsageSeverity health,
        string healthTitle, string healthDetail, string maintenance, IReadOnlyList<SqlMemoryGauge> quotas,
        IReadOnlyList<SqlMemoryStat> stats, IReadOnlyList<SqlMemoryShareBar> servers, string range,
        IReadOnlyList<SqlMemoryActivityLine> activities)
    {
        Capacity = capacity;
        Disk = disk;
        CompactRecommended = compactRecommended;
        Health = health;
        HealthTitle = healthTitle;
        HealthDetail = healthDetail;
        Maintenance = maintenance;
        Quotas = quotas;
        Stats = stats;
        Servers = servers;
        Range = range;
        Activities = activities;
    }

    public SqlMemoryGauge Capacity { get; }
    public string Disk { get; }
    public bool CompactRecommended { get; }
    public SqlMemoryUsageSeverity Health { get; }
    public string HealthTitle { get; }
    public string HealthDetail { get; }
    public string Maintenance { get; }
    public IReadOnlyList<SqlMemoryGauge> Quotas { get; }
    public IReadOnlyList<SqlMemoryStat> Stats { get; }
    public IReadOnlyList<SqlMemoryShareBar> Servers { get; }
    public string Range { get; }
    public IReadOnlyList<SqlMemoryActivityLine> Activities { get; }

    public static SqlMemoryUsageSummary Create(SqlMemoryUsageSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        var report = snapshot.Report;
        var usage = report.Usage;
        var counts = report.Counts;
        var plan = snapshot.Plan;

        var ratio = SqlMemoryCapacity.Ratio(usage.ContentBytes, plan.MaxContentBytes);
        var capacity = new SqlMemoryGauge(SqlMemoryUsageText.ContentGauge,
            plan.MaxContentBytes is { } limit ? Bytes(usage.ContentBytes) + " / " + Bytes(limit) : Bytes(usage.ContentBytes),
            ratio, SqlMemoryCapacity.Severity(ratio),
            ratio is { } value ? SqlMemoryUsageText.ContentRatioDetail(Percent(value)) : SqlMemoryUsageText.ContentUnlimitedDetail);

        var disk = SqlMemoryUsageText.DatabaseFile(Bytes(usage.DatabaseFileBytes)) +
            (usage.WalFileBytes > 0 ? SqlMemoryUsageText.WalFile(Bytes(usage.WalFileBytes)) : "") +
            " · " + SqlMemoryUsageText.Reclaimable(Bytes(report.FreeBytes));

        var (health, healthTitle, healthDetail) = Evaluate(plan, snapshot.State, snapshot.Maintenance, ratio);

        var quotas = new List<SqlMemoryGauge>
        {
            Quota(SqlMemoryUsageText.QuotaExecutions, counts.ExecutionEvents, plan.MaxExecutionEvents, SqlMemoryUsageText.QuotaExecutionsDetail),
            Quota(SqlMemoryUsageText.QuotaFavoriteRevisions, counts.LargestFavoriteRevisions, plan.MaxRevisionsPerFavorite,
                SqlMemoryUsageText.QuotaFavoriteRevisionsDetail),
        };

        var stats = new[]
        {
            new SqlMemoryStat(SqlMemoryUsageText.StatExecutions, Count(counts.ExecutionEntries),
                SqlMemoryUsageText.StatExecutionsDetail(Count(counts.ExecutionEvents))),
            new SqlMemoryStat(SqlMemoryUsageText.StatDrafts, Count(counts.Drafts), SqlMemoryUsageText.StatDraftsDetail(Count(counts.Sessions))),
            new SqlMemoryStat(SqlMemoryUsageText.StatFavorites, Count(counts.Favorites),
                SqlMemoryUsageText.StatFavoritesDetail(Count(counts.FavoriteRevisions))),
            new SqlMemoryStat(SqlMemoryUsageText.StatRecovery, Count(counts.RecoveryItems),
                counts.OpenRecoveryItems > 0
                    ? SqlMemoryUsageText.StatRecoveryOpen(Count(counts.OpenRecoveryItems))
                    : SqlMemoryUsageText.StatRecoveryNoneOpen),
        };

        var top = report.Servers.Length == 0 ? 0 : report.Servers.Max(share => share.Count);
        var servers = report.Servers
            .Select(share => new SqlMemoryShareBar(share.Name, Count(share.Count), top == 0 ? 0 : (double)share.Count / top))
            .ToArray();

        var range = report.OldestAt is { } oldest && report.NewestAt is { } newest
            ? SqlMemoryUsageText.Range(Date(oldest), Date(newest), Count(counts.Contents))
            : SqlMemoryUsageText.RangeEmpty;

        return new SqlMemoryUsageSummary(capacity, disk, report.FreeBytes >= CompactWorthwhileBytes || usage.WalFileBytes >= CompactWorthwhileBytes,
            health, healthTitle, healthDetail, MaintenanceText(snapshot.Maintenance, snapshot.State, now), quotas, stats, servers, range,
            snapshot.Activities.Select(activity => Line(activity, now)).ToArray());
    }

    /// <summary>清除試算的標題；試算是上限，所以說「最多」。</summary>
    public static string CleanupHeadline(SqlMemoryCleanupEstimate estimate) =>
        estimate.Total == 0 ? SqlMemoryUsageText.CleanupNone : SqlMemoryUsageText.CleanupAtMost(Count(estimate.Total));

    /// <summary>清除試算的分類明細；沒有列數的分類不列。</summary>
    public static string CleanupBreakdown(SqlMemoryCleanupEstimate estimate) => string.Join(" · ", new (Func<object?, string> Format, long Rows)[]
        {
            (SqlMemoryUsageText.CleanupExecutions, estimate.ExecutionEntries),
            (SqlMemoryUsageText.CleanupDrafts, estimate.Drafts),
            (SqlMemoryUsageText.CleanupRecovery, estimate.RecoveryItems),
            (SqlMemoryUsageText.CleanupFavoriteRevisions, estimate.FavoriteRevisions),
        }
        .Where(part => part.Rows > 0)
        .Select(part => part.Format(Count(part.Rows))));

    public static string Bytes(long bytes) => SqlAssistDiagnosticReport.FormatBytes(bytes);

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Percent(double ratio) =>
        double.IsInfinity(ratio) ? SqlMemoryUsageText.OverLimit : (ratio * 100).ToString(ratio < 0.1 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%";

    private static (SqlMemoryUsageSeverity, string, string) Evaluate(SqlRetentionSettings plan,
        SqlMemoryMaintenanceState? state, SqlMemoryMaintenanceOverview outlook, double? ratio)
    {
        if (state?.CapacityStatus == SqlMemoryCapacityStatus.CannotReclaimWithinPolicy && ratio >= 1)
            return (SqlMemoryUsageSeverity.Critical, SqlMemoryUsageText.CannotReclaim, SqlMemoryUsageText.CannotReclaimDetail);
        if (ratio >= 1)
            return (SqlMemoryUsageSeverity.Critical, SqlMemoryUsageText.Exceeded, SqlMemoryUsageText.ExceededDetail);
        if (ratio >= SqlMemoryCapacity.CriticalRatio)
            return (SqlMemoryUsageSeverity.Critical, SqlMemoryUsageText.NearLimit, SqlMemoryUsageText.NearLimitDetail);
        if (ratio >= SqlMemoryCapacity.WarningRatio)
            return (SqlMemoryUsageSeverity.Warning, SqlMemoryUsageText.High, SqlMemoryUsageText.HighDetail);
        if (outlook.PendingWork || state?.CapacityStatus == SqlMemoryCapacityStatus.MoreWorkRequired)
            return (SqlMemoryUsageSeverity.Normal, SqlMemoryUsageText.Pending, SqlMemoryUsageText.PendingDetail);
        return (SqlMemoryUsageSeverity.Normal, SqlMemoryUsageText.Healthy,
            plan.MaxContentBytes.HasValue ? SqlMemoryUsageText.HealthyLimitedDetail : SqlMemoryUsageText.HealthyUnlimitedDetail);
    }

    private static SqlMemoryGauge Quota(string label, long used, int? quota, string detail)
    {
        var ratio = quota is { } value ? SqlMemoryCapacity.Ratio(used, value) : null;
        return new SqlMemoryGauge(label, quota is { } limit ? Count(used) + " / " + Count(limit) : SqlMemoryUsageText.Unlimited(Count(used)),
            ratio, SqlMemoryCapacity.Severity(ratio), detail);
    }

    private static string MaintenanceText(SqlMemoryMaintenanceOverview outlook, SqlMemoryMaintenanceState? state, DateTimeOffset now)
    {
        var parts = new List<string>
        {
            outlook.LastMaintainedAt is { } last
                ? SqlMemoryUsageText.LastMaintained(SqlMemoryTimeText.RelativeTime(last, now))
                : SqlMemoryUsageText.NotMaintainedYet,
        };
        if (outlook.NextDueAt is { } next)
        {
            var minutes = (int)Math.Ceiling((next - now).TotalMinutes);
            parts.Add(minutes <= 0
                ? SqlMemoryUsageText.NextMaintenanceSoon
                : SqlMemoryUsageText.NextMaintenanceIn(minutes.ToString(CultureInfo.InvariantCulture)));
        }
        var level = Math.Max(outlook.Level, state?.Round.Level ?? 0);
        if (level > 0)
        {
            var factor = SqlRetentionSettings.Tightening[Math.Min(level, SqlRetentionSettings.Tightening.Count - 1)];
            parts.Add(SqlMemoryUsageText.Tightened(factor.ToString(CultureInfo.InvariantCulture)));
        }
        return string.Join(" · ", parts);
    }

    private static SqlMemoryActivityLine Line(SqlMemoryActivity activity, DateTimeOffset now)
    {
        var title = activity.Kind switch
        {
            SqlMemoryActivityKind.ScheduledMaintenance => SqlMemoryUsageText.ActivityScheduled,
            SqlMemoryActivityKind.ManualMaintenance => SqlMemoryUsageText.ActivityManual,
            SqlMemoryActivityKind.Cleanup => SqlMemoryUsageText.ActivityCleanup,
            SqlMemoryActivityKind.Compact => SqlMemoryUsageText.ActivityCompact,
            SqlMemoryActivityKind.Backup => SqlMemoryUsageText.ActivityBackup,
            _ => SqlMemoryUsageText.ActivityMaintenance,
        };
        var detail = activity.Failure ?? activity.Kind switch
        {
            SqlMemoryActivityKind.Compact => activity.Bytes > 0
                ? SqlMemoryUsageText.CompactShrunk(Bytes(activity.Bytes))
                : SqlMemoryUsageText.CompactNothing,
            SqlMemoryActivityKind.Backup => SqlMemoryUsageText.BackupFile(Bytes(activity.Bytes)),
            _ => activity.DeletedRows <= 0 ? SqlMemoryUsageText.NothingToReclaim
                : activity.Bytes > 0 ? SqlMemoryUsageText.DeletedRowsReleased(Count(activity.DeletedRows), Bytes(activity.Bytes))
                : SqlMemoryUsageText.DeletedRows(Count(activity.DeletedRows)),
        };
        return new SqlMemoryActivityLine(title, detail, SqlMemoryTimeText.RelativeTime(activity.At, now), !activity.Succeeded);
    }

    private static string Date(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
}
