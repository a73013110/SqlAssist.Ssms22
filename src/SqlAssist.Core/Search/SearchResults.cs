using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Search;

/// <summary>
/// 一輪搜尋的結果：排名後的命中，以及每一個目標的完整度。
/// </summary>
public sealed class SearchResults
{
    private static readonly SearchHit[] NoHits = Array.Empty<SearchHit>();
    private static readonly SearchProviderFailure[] NoFailures = Array.Empty<SearchProviderFailure>();
    private static readonly SearchTargetStatus[] NoTargets = Array.Empty<SearchTargetStatus>();

    internal SearchResults(
        long generation,
        IReadOnlyList<SearchHit> hits,
        int totalHits,
        bool isStale,
        bool isCanceled,
        IReadOnlyList<SearchProviderFailure> failures,
        IReadOnlyList<SearchTargetStatus> targets)
    {
        Generation = generation;
        Hits = hits;
        TotalHits = totalHits;
        IsStale = isStale;
        IsCanceled = isCanceled;
        Failures = failures;
        Targets = targets;
    }

    /// <summary>
    /// 已經被更新的一輪取代、整份丟棄的結果。
    /// </summary>
    /// <remarks>
    /// 與「什麼都沒找到」分開，因為畫面上是兩件事：沒找到要顯示「沒有相符項目」，
    /// 過期則應該維持上一份清單，等新的那一輪回來。
    /// </remarks>
    internal static SearchResults Stale(long generation) =>
        new(generation, NoHits, 0, isStale: true, isCanceled: false, NoFailures, NoTargets);

    /// <summary>這份結果屬於哪一輪輸入。</summary>
    public long Generation { get; }

    /// <summary>
    /// 排名後的命中；依 <see cref="SearchMatchTargets.GroupOrder"/> 分組，同一個東西只有一列。
    /// </summary>
    /// <remarks>
    /// 最多 <see cref="SearchAggregator.MaxHits"/> 筆，裁的是排名之後的尾巴；裁過時
    /// <see cref="IsListTruncated"/> 為 true，而 <see cref="TotalHits"/> 仍是真正的筆數。
    /// </remarks>
    public IReadOnlyList<SearchHit> Hits { get; }

    /// <summary>去重之後的真實筆數；清單只列得出 <see cref="Hits"/> 那幾筆時兩者不同。</summary>
    public int TotalHits { get; }

    /// <summary>清單沒有列出全部命中；搜尋本身仍可能是完整的。</summary>
    public bool IsListTruncated => TotalHits > Hits.Count;

    /// <summary>這一輪在跑的時候已經有更新的一輪開始了，內容整份丟棄。</summary>
    public bool IsStale { get; }

    /// <summary>這一輪被取消了（使用者又打了字或按了停止）；沒比完的目標記成已取消。</summary>
    public bool IsCanceled { get; }

    public IReadOnlyList<SearchProviderFailure> Failures { get; }

    /// <summary>每一個目標的結局，依 provider 順序、同一個 provider 內依宣告順序。</summary>
    public IReadOnlyList<SearchTargetStatus> Targets { get; }

    public bool HasFailures => Failures.Count > 0;

    /// <summary>
    /// 每一個目標都比完而且沒有漏；「沒找到」只有在這裡是 true 時才說得出口。
    /// </summary>
    /// <remarks>
    /// 取消、來源擲了例外、任何一個目標讀不到或本文有讀不到的物件，都不算完整。
    /// 清單被裁掉尾巴（<see cref="IsListTruncated"/>）不影響這一個：那是列不下，不是沒搜到。
    /// </remarks>
    public bool IsComplete
    {
        get
        {
            if (IsStale || IsCanceled || Failures.Count != 0) return false;
            foreach (var target in Targets) if (!target.IsComplete) return false;
            return true;
        }
    }
}
