using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Search;

/// <summary>
/// 一輪搜尋的結果：排名後的命中、是否只掃了一部分，以及各來源的狀況。
/// </summary>
public sealed class SearchResults
{
    private static readonly SearchHit[] NoHits = Array.Empty<SearchHit>();
    private static readonly SearchProviderFailure[] NoFailures = Array.Empty<SearchProviderFailure>();
    private static readonly SearchProviderProgress[] NoProgress = Array.Empty<SearchProviderProgress>();

    internal SearchResults(
        long generation,
        IReadOnlyList<SearchHit> hits,
        bool isPartial,
        bool isStale,
        IReadOnlyList<SearchProviderFailure> failures,
        IReadOnlyList<SearchProviderProgress> progress)
    {
        Generation = generation;
        Hits = hits;
        IsPartial = isPartial;
        IsStale = isStale;
        Failures = failures;
        Progress = progress;
    }

    /// <summary>
    /// 已經被更新的一輪取代、整份丟棄的結果。
    /// </summary>
    /// <remarks>
    /// 與「什麼都沒找到」分開，因為畫面上是兩件事：沒找到要顯示「沒有相符項目」，
    /// 過期則應該維持上一份清單，等新的那一輪回來。共用空清單的話，
    /// 使用者每多打一個字都會先閃一次「沒有相符項目」。
    /// </remarks>
    internal static SearchResults Stale(long generation) =>
        new(generation, NoHits, isPartial: false, isStale: true, NoFailures, NoProgress);

    /// <summary>這份結果屬於哪一輪輸入。</summary>
    public long Generation { get; }

    /// <summary>
    /// 排名後的命中；依 <see cref="SearchMatchTargets.GroupOrder"/> 分組，同一個東西只有一列。
    /// </summary>
    public IReadOnlyList<SearchHit> Hits { get; }

    /// <summary>
    /// 沒有掃完：有來源用盡預算、被取消、失敗、整個讀不到，或排名後被總數上限裁掉。
    /// </summary>
    /// <remarks>
    /// 這是一個布林，而畫面上要說的話有三句。要說對是哪一句得再問兩處：
    /// <see cref="Failures"/>（provider 擲了例外）與 <see cref="Progress"/> 上的
    /// <see cref="SearchProviderProgress.IsUnavailable"/>（讀不到，原因在
    /// <see cref="SearchProviderProgress.UnavailableReason"/>）。
    /// 只讀這個布林的症狀是「msdb 沒有權限」被說成「縮小範圍或加長關鍵字」。
    /// </remarks>
    public bool IsPartial { get; }

    /// <summary>這一輪在跑的時候已經有更新的一輪開始了，內容整份丟棄。</summary>
    public bool IsStale { get; }

    public IReadOnlyList<SearchProviderFailure> Failures { get; }

    public IReadOnlyList<SearchProviderProgress> Progress { get; }

    public bool HasFailures => Failures.Count > 0;
}
