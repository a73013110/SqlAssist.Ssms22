using System;
using System.Globalization;

namespace SqlAssist.Core.SqlMemory;

/// <summary>
/// 容量壓力下逐級收緊的保留設定。第一級是日常保留，後續級只能縮短期限與配額，
/// 不新增刪除路徑，也不放寬維護的保護根；分級的實際數值由設定提供。
/// </summary>
public sealed class SqlRetentionLadder
{
    private readonly SqlRetentionPolicy[] _levels;

    public SqlRetentionLadder(params SqlRetentionPolicy[] levels)
    {
        if (levels == null) throw new ArgumentNullException(nameof(levels));
        if (levels.Length == 0) throw new ArgumentException("保留分級至少要有日常那一級。", nameof(levels));
        _levels = new SqlRetentionPolicy[levels.Length];
        for (var level = 0; level < levels.Length; level++)
        {
            _levels[level] = levels[level] ?? throw new ArgumentNullException(nameof(levels));
            if (level > 0) RequireTighter(_levels[level - 1], _levels[level], level);
        }
    }

    public int Count => _levels.Length;

    public SqlRetentionPolicy this[int level] => level >= 0 && level < _levels.Length
        ? _levels[level]
        : throw new ArgumentOutOfRangeException(nameof(level));

    private static void RequireTighter(SqlRetentionPolicy looser, SqlRetentionPolicy tighter, int level)
    {
        // 容量上限是升級的觸發條件而不是分級內容；各級不同會使「超限」在同一輪內換意思。
        if (looser.MaxContentBytes != tighter.MaxContentBytes)
            throw Invalid(level, "容量上限");
        if (Loosens(looser.DraftBefore, tighter.DraftBefore)) throw Invalid(level, "草稿截止時間");
        if (Loosens(looser.ExecutionBefore, tighter.ExecutionBefore)) throw Invalid(level, "執行截止時間");
        if (Loosens(looser.RecoveryBefore, tighter.RecoveryBefore)) throw Invalid(level, "未存檔回復內容截止時間");
        if (Loosens(looser.MaxExecutionEvents, tighter.MaxExecutionEvents)) throw Invalid(level, "執行筆數配額");
        if (Loosens(looser.MaxAutoRevisionsPerSession, tighter.MaxAutoRevisionsPerSession)) throw Invalid(level, "每 Session 版本配額");
        if (Loosens(looser.MaxRevisionsPerFavorite, tighter.MaxRevisionsPerFavorite)) throw Invalid(level, "每 Favorite 版本配額");
    }

    // null 截止時間停用該類清理，是最寬鬆的一端；收緊只能往現在靠，不能退回 null。
    private static bool Loosens(DateTimeOffset? looser, DateTimeOffset? tighter) =>
        looser.HasValue && (!tighter.HasValue || tighter.Value < looser.Value);

    private static bool Loosens(int? looser, int? tighter) =>
        looser.HasValue && (!tighter.HasValue || tighter.Value > looser.Value);

    private static ArgumentException Invalid(int level, string field) => new(
        "保留分級 " + level.ToString(CultureInfo.InvariantCulture) + " 的" + field + "比前一級寬鬆。", "levels");
}

/// <summary>下一批要跑的輪次、由輪次換算出的政策，以及接續的游標。</summary>
public sealed record SqlMemoryMaintenanceBatch(SqlMemoryMaintenanceRound Round, SqlRetentionPolicy Policy,
    string? Cursor);

/// <summary>一批寫回狀態之後，下一批會用哪一級，以及是否該把下一批排近。</summary>
public sealed record SqlMemoryMaintenanceOutlook(int Level, bool PendingWork);

/// <summary>
/// 依共用維護狀態決定下一批：沿用未巡完的輪次，或在輪次邊界套用上一輪的結論後開新輪次。
/// </summary>
/// <remarks>
/// 本身不保存進度：級數、輪次起點與游標都在儲存層的狀態表，換程序或重啟 SSMS 都從那裡接。
/// 輪次起點決定整條分級的截止時間，接續同一輪時政策逐位元組相同，游標才對得上；
/// 每開新輪次都以當下換算並保留壓力級，壓力持續期間截止時間才不會凍結。
/// 本身不計時、不呼叫 repository，排程由宿主負責。
/// </remarks>
public sealed class SqlMemoryMaintenancePlanner
{
    /// <summary>每完成這麼多輪索引巡查就做一次完整掃描，回收索引路徑碰不到的孤立資料。</summary>
    public const int FullScanEveryRounds = 24;

    public SqlMemoryMaintenancePlanner(SqlRetentionSettings plan) =>
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));

    public SqlRetentionSettings Plan { get; }

    private static int LevelCount => SqlRetentionSettings.Tightening.Count;

    /// <param name="state">儲存層最後寫回的狀態；null 表示從來沒有人跑過。</param>
    /// <param name="reclaimRecovery">本程序此刻是否真的持有 Session 心跳租約。</param>
    public SqlMemoryMaintenanceBatch Next(SqlMemoryMaintenanceState? state, DateTimeOffset now, bool reclaimRecovery)
    {
        var sinceFullScan = state?.Round.RoundsSinceFullScan ?? 0;
        // 設定一改，舊輪次的政策與壓力級都是依舊設定推出來的，沿用只會拿舊結論套新設定。
        if (state == null || state.Round.PlanFingerprint != Plan.Fingerprint || state.Round.Level >= LevelCount)
            return Start(0, SqlMemoryMaintenanceScan.Indexed, sinceFullScan, now, reclaimRecovery);

        var round = state.Round;
        // 心跳斷掉時立刻放棄這一輪，即使正巡到一半：多巡一輪的成本，
        // 遠小於讓沒有租約的線上 Session 的回復內容被當成遺留資料。
        if (round.ReclaimsRecovery && !reclaimRecovery)
            return Start(0, SqlMemoryMaintenanceScan.Indexed, sinceFullScan, now, false);

        // 起點晚於現在代表時鐘曾被往回調；照舊起點換算的截止時間會落在真實的未來，把期限內的資料當成過期。
        if (state.Cursor != null && round.StartedAt > now) return Restart(round, now, reclaimRecovery);
        if (state.Cursor != null) return Batch(round, state.Cursor);

        var next = Conclude(state);
        return Start(next.Level, next.Scan, next.RoundsSinceFullScan, now, reclaimRecovery);
    }

    /// <summary>接續的游標被儲存層拒絕時，保留級數與掃描方式從當下重開這一輪；舊游標沒有續讀價值。</summary>
    public SqlMemoryMaintenanceBatch Restart(SqlMemoryMaintenanceRound round, DateTimeOffset now, bool reclaimRecovery)
    {
        if (round == null) throw new ArgumentNullException(nameof(round));
        return Start(Math.Min(round.Level, LevelCount - 1), round.Scan, round.RoundsSinceFullScan, now, reclaimRecovery);
    }

    /// <summary>只看寫回的狀態推算，與下一個程序讀到同一份狀態時得到的結論相同。</summary>
    public SqlMemoryMaintenanceOutlook Observe(SqlMemoryMaintenanceState state)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (state.Cursor != null) return new SqlMemoryMaintenanceOutlook(state.Round.Level, true);
        var next = Conclude(state);
        return new SqlMemoryMaintenanceOutlook(next.Level, next.Urgent);
    }

    private SqlMemoryMaintenanceBatch Start(int level, SqlMemoryMaintenanceScan scan, int sinceFullScan,
        DateTimeOffset now, bool reclaimRecovery) =>
        Batch(new SqlMemoryMaintenanceRound(Plan.Fingerprint, now, level, reclaimRecovery, scan, sinceFullScan), null);

    private SqlMemoryMaintenanceBatch Batch(SqlMemoryMaintenanceRound round, string? cursor) => new(round,
        Plan.BuildLadder(round.StartedAt, round.ReclaimsRecovery)[round.Level], cursor);

    /// <summary>容量回到上限內就直接回日常級，不逐級退回；只在輪次邊界換級。</summary>
    private static (int Level, SqlMemoryMaintenanceScan Scan, int RoundsSinceFullScan, bool Urgent) Conclude(
        SqlMemoryMaintenanceState state)
    {
        var round = state.Round;
        var since = round.Scan == SqlMemoryMaintenanceScan.Full ? 0 : Math.Min(round.RoundsSinceFullScan + 1, FullScanEveryRounds);
        var routine = since >= FullScanEveryRounds ? SqlMemoryMaintenanceScan.Full : SqlMemoryMaintenanceScan.Indexed;
        if (state.CapacityStatus == SqlMemoryCapacityStatus.WithinLimit)
            return (0, routine, since, state.RequiresAnotherPass);
        if (state.CapacityStatus != SqlMemoryCapacityStatus.CannotReclaimWithinPolicy || round.Level + 1 >= LevelCount)
            // 最緊的一級也回收不到就是政策擋住了容量，不該一直排下一批空轉。
            return (round.Level, routine, since, state.RequiresAnotherPass);
        // 索引巡查看不到索引以外的孤立資料；升級前先完整掃描一輪，確認這一級真的回收不到。
        return round.Scan == SqlMemoryMaintenanceScan.Indexed
            ? (round.Level, SqlMemoryMaintenanceScan.Full, since, true)
            : (round.Level + 1, SqlMemoryMaintenanceScan.Indexed, since, true);
    }
}
