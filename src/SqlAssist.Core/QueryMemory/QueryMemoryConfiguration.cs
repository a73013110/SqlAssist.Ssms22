using System;
using SqlAssist.Core.Settings;

namespace SqlAssist.Core.QueryMemory;

/// <summary>
/// 一次設定變更收斂成的不可變快照：開關、擷取政策、保留計畫與排程間隔。
/// </summary>
/// <remarks>
/// 設定轉政策的規則集中在這裡，宿主只負責讀設定服務；換設定時整份換掉，
/// 背景工作拿到的永遠是一致的一組，不會讀到一半新一半舊的值。
/// </remarks>
public sealed class QueryMemoryConfiguration
{
    private QueryMemoryConfiguration(bool enabled, QueryMemoryPolicy policy, QueryMemoryRetentionPlan plan,
        TimeSpan maintenanceInterval, TimeSpan idleDebounce)
    {
        Enabled = enabled;
        Policy = policy;
        Plan = plan;
        MaintenanceInterval = maintenanceInterval;
        IdleDebounce = idleDebounce;
    }

    /// <summary>尚未讀到設定時的狀態；預設是關的——擷取的是使用者輸入的 SQL，沒有明確打開就不該存。</summary>
    public static QueryMemoryConfiguration Disabled { get; } = From(new SqlAssistSettings { QueryMemoryEnabled = false });

    /// <summary>SqlAssist 總開關與 SQL Memory 開關都開著才啟用；關掉時連背景整理都不跑，那也是在動使用者的資料。</summary>
    public bool Enabled { get; }

    public QueryMemoryPolicy Policy { get; }

    public QueryMemoryRetentionPlan Plan { get; }

    public TimeSpan MaintenanceInterval { get; }

    /// <summary>閒置提前另有最小間隔，免得連續閒置變成忙碌迴圈。</summary>
    public TimeSpan IdleMinimumGap => TimeSpan.FromTicks(MaintenanceInterval.Ticks / 4);

    /// <summary>停止輸入多久之後記下草稿。</summary>
    public TimeSpan IdleDebounce { get; }

    public static QueryMemoryConfiguration From(SqlAssistSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        var recovery = settings.QueryMemoryCaptureDrafts && settings.QueryMemoryRecoverUnsavedDrafts;
        var policy = new QueryMemoryPolicy(recovery, settings.QueryMemoryCaptureDrafts,
            TimeSpan.FromMinutes(settings.QueryMemoryAutoRevisionMinutes), settings.QueryMemoryCaptureExecuted,
            settings.QueryMemoryCaptureDrafts);

        // 沒有在保留未存檔草稿就不給期限：有期限才需要回收，沒有就完全不憑年齡刪。
        var plan = new QueryMemoryRetentionPlan(
            TimeSpan.FromDays(settings.QueryMemoryDraftRetentionDays),
            TimeSpan.FromDays(settings.QueryMemoryExecutionRetentionDays),
            recovery ? TimeSpan.FromDays(settings.QueryMemoryUnsavedDraftRetentionDays) : null,
            QueryMemoryRetentionPlan.ToContentBytes(settings.QueryMemoryStorage),
            settings.QueryMemoryMaxExecutions,
            settings.QueryMemoryMaxSessionRevisions,
            settings.QueryMemoryMaxFavoriteRevisions);

        return new QueryMemoryConfiguration(settings.Enabled && settings.QueryMemoryEnabled, policy, plan,
            TimeSpan.FromMinutes(settings.QueryMemoryMaintenanceMinutes), TimeSpan.FromSeconds(settings.QueryMemoryIdleSeconds));
    }
}
