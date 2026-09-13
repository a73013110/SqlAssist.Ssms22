using System;
using System.Globalization;

namespace SqlAssist.Core.QueryMemory;

/// <summary>
/// 容量壓力下逐級收緊的保留設定。第一級是日常保留，後續級只能縮短期限與配額，
/// 不新增刪除路徑，也不放寬維護的保護根；分級的實際數值由設定提供。
/// </summary>
public sealed class QueryMemoryRetentionLadder
{
    private readonly QueryMemoryMaintenancePolicy[] _levels;

    public QueryMemoryRetentionLadder(params QueryMemoryMaintenancePolicy[] levels)
    {
        if (levels == null) throw new ArgumentNullException(nameof(levels));
        if (levels.Length == 0) throw new ArgumentException("保留分級至少要有日常那一級。", nameof(levels));
        _levels = new QueryMemoryMaintenancePolicy[levels.Length];
        for (var level = 0; level < levels.Length; level++)
        {
            _levels[level] = levels[level] ?? throw new ArgumentNullException(nameof(levels));
            if (level > 0) RequireTighter(_levels[level - 1], _levels[level], level);
        }
    }

    public int Count => _levels.Length;

    public QueryMemoryMaintenancePolicy this[int level] => level >= 0 && level < _levels.Length
        ? _levels[level]
        : throw new ArgumentOutOfRangeException(nameof(level));

    private static void RequireTighter(QueryMemoryMaintenancePolicy looser, QueryMemoryMaintenancePolicy tighter, int level)
    {
        // 容量上限是升級的觸發條件而不是分級內容；各級不同會使「超限」在同一輪內換意思。
        if (looser.MaxContentBytes != tighter.MaxContentBytes)
            throw Invalid(level, "容量上限");
        if (Loosens(looser.DraftBefore, tighter.DraftBefore)) throw Invalid(level, "草稿截止時間");
        if (Loosens(looser.ExecutionBefore, tighter.ExecutionBefore)) throw Invalid(level, "執行截止時間");
        if (Loosens(looser.RecoveryBefore, tighter.RecoveryBefore)) throw Invalid(level, "未存檔草稿截止時間");
        if (Loosens(looser.MaxExecutionEvents, tighter.MaxExecutionEvents)) throw Invalid(level, "執行筆數配額");
        if (Loosens(looser.MaxAutoRevisionsPerSession, tighter.MaxAutoRevisionsPerSession)) throw Invalid(level, "每 Session 版本配額");
        if (Loosens(looser.MaxRevisionsPerFavoriteQuery, tighter.MaxRevisionsPerFavoriteQuery)) throw Invalid(level, "每 Favorite 版本配額");
    }

    // null 截止時間停用該類清理，是最寬鬆的一端；收緊只能往現在靠，不能退回 null。
    private static bool Loosens(DateTimeOffset? looser, DateTimeOffset? tighter) =>
        looser.HasValue && (!tighter.HasValue || tighter.Value < looser.Value);

    private static bool Loosens(int? looser, int? tighter) =>
        looser.HasValue && (!tighter.HasValue || tighter.Value > looser.Value);

    private static ArgumentException Invalid(int level, string field) => new(
        "保留分級 " + level.ToString(CultureInfo.InvariantCulture) + " 的" + field + "比前一級寬鬆。", "levels");
}

/// <summary>
/// 依上一輪結果決定下一批維護要用哪一級保留與哪個游標。只在完成一輪的邊界升降級，
/// 游標因此不會跨級沿用；本身不計時、不呼叫 repository，排程另由宿主負責。
/// </summary>
public sealed class QueryMemoryMaintenancePlanner
{
    private readonly QueryMemoryRetentionLadder _ladder;

    public QueryMemoryMaintenancePlanner(QueryMemoryRetentionLadder ladder) =>
        _ladder = ladder ?? throw new ArgumentNullException(nameof(ladder));

    /// <summary>目前分級；0 是日常保留。容量回到上限內就直接回到 0，不逐級退回。</summary>
    public int Level { get; private set; }

    public QueryMemoryMaintenancePolicy Policy => _ladder[Level];

    public string? Cursor { get; private set; }

    /// <summary>該儘快再跑一批：這一輪未巡完、有刪除要再巡一輪，或剛升級換了保留設定。</summary>
    public bool PendingWork { get; private set; }

    public QueryMemoryMaintenanceRequest NextRequest(int candidateLimit) =>
        new(Policy, candidateLimit, Cursor);

    /// <summary>只接受 <see cref="NextRequest"/> 這一輪的結果；跨政策沿用結果會誤判壓力。</summary>
    public void Observe(QueryMemoryMaintenanceResult result)
    {
        if (result == null) throw new ArgumentNullException(nameof(result));
        Cursor = result.Cursor;
        if (result.Cursor != null) { PendingWork = true; return; }
        PendingWork = result.RequiresAnotherPass;
        if (result.CapacityStatus == QueryMemoryCapacityStatus.WithinLimit) { Level = 0; return; }
        // 只有完整巡過一輪仍沒有東西可回收，才承認這一級的保留擋住了容量而換下一級。
        if (result.CapacityStatus != QueryMemoryCapacityStatus.CannotReclaimWithinPolicy || Level + 1 >= _ladder.Count) return;
        Level++;
        PendingWork = true;
    }

    /// <summary>設定改變時重新開始；舊游標綁在舊政策上，沿用會被 repository 拒絕。</summary>
    public void Reset()
    {
        Level = 0;
        Cursor = null;
        PendingWork = false;
    }
}
