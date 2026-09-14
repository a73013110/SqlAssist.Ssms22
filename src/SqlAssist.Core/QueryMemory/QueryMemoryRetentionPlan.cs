using System;
using System.Collections.Generic;
using System.Globalization;

namespace SqlAssist.Core.QueryMemory;

/// <summary>設定頁提供的儲存上限級距；清單型資料進不了 Unified Settings，所以只給幾個固定值。</summary>
public enum QueryMemoryStorageLimit { Megabytes256, Megabytes512, Gigabytes1, Unlimited }

/// <summary>
/// 日常那一級的保留設定，也是整條保留分級的唯一輸入。
/// </summary>
/// <remarks>
/// 期限存成長度而不是絕對時間：截止時間每一輪重新換算，存成絕對時間的話設定讀進來
/// 之後就停在那一刻，SSMS 開著幾天就再也清不掉東西。
///
/// 容量壓力下的收緊分級<b>不是設定</b>。有序的多級資料放不進 Unified Settings，
/// 而拆成「第二級幾天、第三級幾天」的純量設定等於要使用者回答一個他沒有依據的問題；
/// 使用者感覺得到的是日常保留多久。因此只有日常那一級來自設定，其餘由
/// <see cref="Tightening"/> 這組固定倍率推出來。代價是壓力下的收緊速度不可調——
/// 要更早收緊只能把日常保留調短。
/// </remarks>
public sealed class QueryMemoryRetentionPlan
{
    /// <summary>各級的收緊倍率；第一項是日常保留，必須是 1。只能往下，不能放寬。</summary>
    /// <remarks>公開陣列可被任何呼叫端改寫，整個程序的分級就跟著變；因此只給唯讀檢視。</remarks>
    public static IReadOnlyList<int> Tightening { get; } = Array.AsReadOnly(new[] { 1, 2, 4 });

    /// <summary>收緊到零等於「全部刪掉」，那不是壓力分級該做的事。</summary>
    private static readonly TimeSpan MinimumRetention = TimeSpan.FromDays(1);

    public QueryMemoryRetentionPlan(TimeSpan draftRetention, TimeSpan executionRetention,
        TimeSpan? unsavedDraftRetention, long? maxContentBytes, int? maxExecutionEvents,
        int? maxAutoRevisionsPerSession, int? maxRevisionsPerFavoriteQuery)
    {
        if (draftRetention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(draftRetention));
        if (executionRetention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(executionRetention));
        if (unsavedDraftRetention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(unsavedDraftRetention));
        if (maxContentBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxContentBytes));
        if (maxExecutionEvents < 0) throw new ArgumentOutOfRangeException(nameof(maxExecutionEvents));
        if (maxAutoRevisionsPerSession < 0) throw new ArgumentOutOfRangeException(nameof(maxAutoRevisionsPerSession));
        if (maxRevisionsPerFavoriteQuery < 0) throw new ArgumentOutOfRangeException(nameof(maxRevisionsPerFavoriteQuery));
        DraftRetention = draftRetention;
        ExecutionRetention = executionRetention;
        UnsavedDraftRetention = unsavedDraftRetention;
        MaxContentBytes = maxContentBytes;
        MaxExecutionEvents = maxExecutionEvents;
        MaxAutoRevisionsPerSession = maxAutoRevisionsPerSession;
        MaxRevisionsPerFavoriteQuery = maxRevisionsPerFavoriteQuery;
        // 收緊倍率與下限也決定換算結果；只比設定值的話，改版後同一輪接續會算出不同政策而游標作廢。
        Fingerprint = string.Join("|", "retention1", Number(draftRetention.Ticks), Number(executionRetention.Ticks),
            Number(unsavedDraftRetention?.Ticks), Number(maxContentBytes), Number(maxExecutionEvents),
            Number(maxAutoRevisionsPerSession), Number(maxRevisionsPerFavoriteQuery),
            string.Join(",", Tightening), Number(MinimumRetention.Ticks));
    }

    /// <summary>整條分級的全部輸入；持久化的維護輪次只在指紋相同時接續，設定一改就從新輪次開始。</summary>
    public string Fingerprint { get; }

    public TimeSpan DraftRetention { get; }
    public TimeSpan ExecutionRetention { get; }

    /// <summary>未存檔草稿的保留長度；null 表示完全不憑年齡回收，宿主沒有在續心跳時就該是 null。</summary>
    public TimeSpan? UnsavedDraftRetention { get; }

    public long? MaxContentBytes { get; }
    public int? MaxExecutionEvents { get; }
    public int? MaxAutoRevisionsPerSession { get; }
    public int? MaxRevisionsPerFavoriteQuery { get; }

    public static long? ToContentBytes(QueryMemoryStorageLimit limit) => limit switch
    {
        QueryMemoryStorageLimit.Megabytes256 => 256L * 1024 * 1024,
        QueryMemoryStorageLimit.Megabytes512 => 512L * 1024 * 1024,
        QueryMemoryStorageLimit.Gigabytes1 => 1024L * 1024 * 1024,
        QueryMemoryStorageLimit.Unlimited => null,
        _ => throw new ArgumentOutOfRangeException(nameof(limit)),
    };

    /// <summary>
    /// 以這一刻換算出整條保留分級。
    /// </summary>
    /// <param name="now">換算截止時間的基準；同一輪維護必須沿用同一個，游標才綁得住政策。</param>
    /// <param name="reclaimUnsavedDrafts">
    /// 宿主是否真的在續 Session 心跳。false 時整條分級的 Recovery 截止時間都是 null——
    /// 沒有租約的線上 Session 會被當成遺留資料，使用者還開著的未存檔內容就消失了。
    /// </param>
    public QueryMemoryRetentionLadder BuildLadder(DateTimeOffset now, bool reclaimUnsavedDrafts)
    {
        var levels = new QueryMemoryMaintenancePolicy[Tightening.Count];
        for (var level = 0; level < levels.Length; level++)
        {
            var divisor = Tightening[level];
            var recovery = reclaimUnsavedDrafts && UnsavedDraftRetention is { } unsaved
                ? now - Shorten(unsaved, divisor)
                : (DateTimeOffset?)null;
            levels[level] = new QueryMemoryMaintenancePolicy(
                now - Shorten(DraftRetention, divisor),
                now - Shorten(ExecutionRetention, divisor),
                MaxContentBytes,
                Reduce(MaxExecutionEvents, divisor),
                Reduce(MaxAutoRevisionsPerSession, divisor),
                Reduce(MaxRevisionsPerFavoriteQuery, divisor),
                recovery);
        }

        return new QueryMemoryRetentionLadder(levels);
    }

    private static TimeSpan Shorten(TimeSpan span, int divisor)
    {
        if (divisor <= 1) return span;
        // 日常保留本來就短於下限時，下限就是它自己：分級只能收緊，不能反而變長。
        var floor = span < MinimumRetention ? span : MinimumRetention;
        var shortened = TimeSpan.FromTicks(span.Ticks / divisor);
        return shortened < floor ? floor : shortened;
    }

    // 配額 0 表示全部超額，已經是最緊的一端；除下去反而會放寬成 1。
    private static int? Reduce(int? quota, int divisor) =>
        quota is { } value && divisor > 1 && value > 1 ? Math.Max(1, value / divisor) : quota;

    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
