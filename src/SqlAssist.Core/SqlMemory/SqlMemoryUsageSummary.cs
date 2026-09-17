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
        var capacity = new SqlMemoryGauge("SQL 內容",
            plan.MaxContentBytes is { } limit ? Bytes(usage.ContentBytes) + " / " + Bytes(limit) : Bytes(usage.ContentBytes),
            ratio, SqlMemoryCapacity.Severity(ratio),
            ratio is { } value ? Percent(value) + "，去重後的 SQL 文字；不含索引與中繼資料。" : "容量不限；去重後的 SQL 文字，不含索引與中繼資料。");

        var disk = "資料庫檔案 " + Bytes(usage.DatabaseFileBytes) +
            (usage.WalFileBytes > 0 ? "（另有 WAL " + Bytes(usage.WalFileBytes) + "）" : "") +
            " · 可回收約 " + Bytes(report.FreeBytes);

        var (health, healthTitle, healthDetail) = Evaluate(plan, snapshot.State, snapshot.Maintenance, ratio);

        var quotas = new List<SqlMemoryGauge>
        {
            Quota("執行事件", counts.ExecutionEvents, plan.MaxExecutionEvents, "超過配額的最舊執行由背景維護回收。"),
            Quota("單一收藏版本", counts.LargestFavoriteRevisions, plan.MaxRevisionsPerFavorite, "目前版本永遠保留。"),
        };

        var stats = new[]
        {
            new SqlMemoryStat("執行紀錄", Count(counts.ExecutionEntries), "共 " + Count(counts.ExecutionEvents) + " 次執行"),
            new SqlMemoryStat("草稿", Count(counts.Drafts), Count(counts.Sessions) + " 個編輯工作階段"),
            new SqlMemoryStat("收藏", Count(counts.Favorites), Count(counts.FavoriteRevisions) + " 個版本"),
            new SqlMemoryStat("回復內容", Count(counts.RecoveryItems),
                counts.OpenRecoveryItems > 0 ? Count(counts.OpenRecoveryItems) + " 個視窗仍開著" : "沒有開著的視窗"),
        };

        var top = report.Servers.Length == 0 ? 0 : report.Servers.Max(share => share.Count);
        var servers = report.Servers
            .Select(share => new SqlMemoryShareBar(share.Name, Count(share.Count), top == 0 ? 0 : (double)share.Count / top))
            .ToArray();

        var range = report.OldestAt is { } oldest && report.NewestAt is { } newest
            ? "紀錄範圍 " + Date(oldest) + " – " + Date(newest) + " · " + Count(counts.Contents) + " 份不重複的 SQL"
            : "還沒有任何紀錄";

        return new SqlMemoryUsageSummary(capacity, disk, report.FreeBytes >= CompactWorthwhileBytes || usage.WalFileBytes >= CompactWorthwhileBytes,
            health, healthTitle, healthDetail, MaintenanceText(snapshot.Maintenance, snapshot.State, now), quotas, stats, servers, range,
            snapshot.Activities.Select(activity => Line(activity, now)).ToArray());
    }

    /// <summary>清除試算的標題；試算是上限，所以說「最多」。</summary>
    public static string CleanupHeadline(SqlMemoryCleanupEstimate estimate) =>
        estimate.Total == 0 ? "沒有符合條件的紀錄" : "最多清除 " + Count(estimate.Total) + " 筆";

    /// <summary>清除試算的分類明細；沒有列數的分類不列。</summary>
    public static string CleanupBreakdown(SqlMemoryCleanupEstimate estimate) => string.Join(" · ", new[]
        {
            ("執行紀錄", estimate.ExecutionEntries), ("草稿", estimate.Drafts),
            ("回復內容", estimate.RecoveryItems), ("收藏版本", estimate.FavoriteRevisions),
        }
        .Where(part => part.Item2 > 0)
        .Select(part => part.Item1 + " " + Count(part.Item2)));

    public static string Bytes(long bytes) => SqlAssistDiagnosticReport.FormatBytes(bytes);

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Percent(double ratio) =>
        double.IsInfinity(ratio) ? "超過上限" : (ratio * 100).ToString(ratio < 0.1 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%";

    private static (SqlMemoryUsageSeverity, string, string) Evaluate(SqlRetentionSettings plan,
        SqlMemoryMaintenanceState? state, SqlMemoryMaintenanceOverview outlook, double? ratio)
    {
        if (state?.CapacityStatus == SqlMemoryCapacityStatus.CannotReclaimWithinPolicy && ratio >= 1)
            return (SqlMemoryUsageSeverity.Critical, "目前的保留規則清不下來",
                "收藏、仍開著的草稿與版本鏈受保護，背景維護已找不到可以回收的資料。請手動清理、縮短保留期限或提高容量上限。");
        if (ratio >= 1)
            return (SqlMemoryUsageSeverity.Critical, "已超過容量上限",
                "背景維護會逐級收緊保留期限回收空間；也可以立即維護或手動清理舊紀錄。");
        if (ratio >= SqlMemoryCapacity.CriticalRatio)
            return (SqlMemoryUsageSeverity.Critical, "容量接近上限", "建議立即維護或清理不再需要的紀錄。");
        if (ratio >= SqlMemoryCapacity.WarningRatio)
            return (SqlMemoryUsageSeverity.Warning, "容量偏高", "背景維護會照保留規則回收；需要空間時可以手動清理。");
        if (outlook.PendingWork || state?.CapacityStatus == SqlMemoryCapacityStatus.MoreWorkRequired)
            return (SqlMemoryUsageSeverity.Normal, "背景整理中", "還有過期的紀錄待回收，會在接下來幾分鐘內分批完成。");
        return (SqlMemoryUsageSeverity.Normal, "狀態良好",
            plan.MaxContentBytes.HasValue ? "容量在上限內，背景維護照保留規則運作。" : "容量不限，背景維護照保留期限運作。");
    }

    private static SqlMemoryGauge Quota(string label, long used, int? quota, string detail)
    {
        var ratio = quota is { } value ? SqlMemoryCapacity.Ratio(used, value) : null;
        return new SqlMemoryGauge(label, quota is { } limit ? Count(used) + " / " + Count(limit) : Count(used) + "（不限）",
            ratio, SqlMemoryCapacity.Severity(ratio), detail);
    }

    private static string MaintenanceText(SqlMemoryMaintenanceOverview outlook, SqlMemoryMaintenanceState? state, DateTimeOffset now)
    {
        var parts = new List<string>
        {
            outlook.LastMaintainedAt is { } last ? "上次維護 " + SqlMemoryTimeText.RelativeTime(last, now) : "本次開啟後尚未維護",
        };
        if (outlook.NextDueAt is { } next)
        {
            var minutes = (int)Math.Ceiling((next - now).TotalMinutes);
            parts.Add(minutes <= 0 ? "下次維護即將開始" : "下次約 " + minutes.ToString(CultureInfo.InvariantCulture) + " 分鐘後");
        }
        var level = Math.Max(outlook.Level, state?.Round.Level ?? 0);
        if (level > 0) parts.Add("容量壓力，保留期限已收緊為 1/" + SqlRetentionSettings.Tightening[Math.Min(level, SqlRetentionSettings.Tightening.Count - 1)].ToString(CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }

    private static SqlMemoryActivityLine Line(SqlMemoryActivity activity, DateTimeOffset now)
    {
        var title = activity.Kind switch
        {
            SqlMemoryActivityKind.ScheduledMaintenance => "背景維護",
            SqlMemoryActivityKind.ManualMaintenance => "立即維護",
            SqlMemoryActivityKind.Cleanup => "手動清理",
            SqlMemoryActivityKind.Compact => "壓縮資料庫",
            SqlMemoryActivityKind.Backup => "備份",
            _ => "維護",
        };
        var detail = activity.Failure ?? activity.Kind switch
        {
            SqlMemoryActivityKind.Compact => activity.Bytes > 0 ? "檔案縮小 " + Bytes(activity.Bytes) : "沒有可回收的空間",
            SqlMemoryActivityKind.Backup => "備份檔 " + Bytes(activity.Bytes),
            _ => activity.DeletedRows > 0
                ? "刪除 " + Count(activity.DeletedRows) + " 列" + (activity.Bytes > 0 ? "，釋出 " + Bytes(activity.Bytes) : "")
                : "沒有需要回收的資料",
        };
        return new SqlMemoryActivityLine(title, detail, SqlMemoryTimeText.RelativeTime(activity.At, now), !activity.Succeeded);
    }

    private static string Date(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
}
