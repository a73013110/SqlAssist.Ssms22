using System;
using SqlAssist.Core.Settings;

namespace SqlAssist.Core.SqlMemory;

/// <summary>
/// 一次設定變更收斂成的不可變快照：開關、擷取政策、保留計畫與排程間隔。
/// </summary>
/// <remarks>
/// 設定轉政策的規則集中在這裡，宿主只負責讀設定服務；換設定時整份換掉，
/// 背景工作拿到的永遠是一致的一組，不會讀到一半新一半舊的值。
/// </remarks>
public sealed class SqlMemoryConfiguration
{
    private SqlMemoryConfiguration(bool enabled, SqlCapturePolicy policy, SqlRetentionSettings plan,
        TimeSpan maintenanceInterval, TimeSpan idleDebounce)
    {
        Enabled = enabled;
        Policy = policy;
        Plan = plan;
        MaintenanceInterval = maintenanceInterval;
        IdleDebounce = idleDebounce;
    }

    /// <summary>尚未讀到設定時的狀態；預設是關的——擷取的是使用者輸入的 SQL，沒有明確打開就不該存。</summary>
    public static SqlMemoryConfiguration Disabled { get; } = From(new SqlAssistSettings { SqlMemoryEnabled = false });

    /// <summary>SqlAssist 總開關與 SQL Memory 開關都開著才啟用；關掉時連背景整理都不跑，那也是在動使用者的資料。</summary>
    public bool Enabled { get; }

    public SqlCapturePolicy Policy { get; }

    public SqlRetentionSettings Plan { get; }

    public TimeSpan MaintenanceInterval { get; }

    /// <summary>閒置提前另有最小間隔，免得連續閒置變成忙碌迴圈。</summary>
    public TimeSpan IdleMinimumGap => TimeSpan.FromTicks(MaintenanceInterval.Ticks / 4);

    /// <summary>停止輸入多久之後記下草稿。</summary>
    public TimeSpan IdleDebounce { get; }

    public static SqlMemoryConfiguration From(SqlAssistSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        var recovery = settings.SqlMemoryCaptureDrafts && settings.SqlMemoryCaptureRecovery;
        var policy = new SqlCapturePolicy(recovery, settings.SqlMemoryCaptureDrafts,
            TimeSpan.FromMinutes(settings.SqlMemoryAutoRevisionMinutes), settings.SqlMemoryCaptureExecuted,
            settings.SqlMemoryCaptureDrafts);

        // 沒有在保留未存檔草稿就不給期限：有期限才需要回收，沒有就完全不憑年齡刪。
        var plan = new SqlRetentionSettings(
            TimeSpan.FromDays(settings.SqlMemoryDraftRetentionDays),
            TimeSpan.FromDays(settings.SqlMemoryExecutionRetentionDays),
            recovery ? TimeSpan.FromDays(settings.SqlMemoryRecoveryRetentionDays) : null,
            SqlRetentionSettings.ToContentBytes(settings.SqlMemoryStorage),
            settings.SqlMemoryMaxExecutions,
            settings.SqlMemoryMaxSessionRevisions,
            settings.SqlMemoryMaxFavoriteRevisions);

        return new SqlMemoryConfiguration(settings.Enabled && settings.SqlMemoryEnabled, policy, plan,
            TimeSpan.FromMinutes(settings.SqlMemoryMaintenanceMinutes), TimeSpan.FromSeconds(settings.SqlMemoryIdleSeconds));
    }
}
