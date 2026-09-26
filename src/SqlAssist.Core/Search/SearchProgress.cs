using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Search;

/// <summary>
/// 一輪搜尋在某一刻的進度；畫面的計時器每隔一小段時間問一次。
/// </summary>
/// <remarks>
/// 由畫面來問而不是 provider 推：建索引那一段一秒可以讀上萬列，每一列推一次事件等於
/// 把 UI 執行緒塞滿；問的頻率由畫面決定（約每秒十次），provider 只寫一個數字。
/// </remarks>
public sealed class SearchProgress
{
    internal SearchProgress(IReadOnlyList<SearchTargetStatus> targets, int hitCount)
    {
        Targets = targets;
        HitCount = hitCount;

        var sum = 0.0;
        foreach (var target in targets)
        {
            sum += target.Progress;
            if (target.State != SearchTargetState.Running) Finished++;
        }

        Fraction = targets.Count == 0 ? 0 : Math.Min(1, sum / targets.Count);
    }

    public IReadOnlyList<SearchTargetStatus> Targets { get; }

    /// <summary>到目前為止收到的命中（去重之前）。</summary>
    public int HitCount { get; }

    /// <summary>已經有結局的目標數。</summary>
    public int Finished { get; }

    public int Total => Targets.Count;

    /// <summary>
    /// 整輪走到哪裡，0 到 1；每個目標等權。
    /// </summary>
    /// <remarks>
    /// 不照資料庫大小加權：大小要等索引第一段回來才知道，而那時一半的目標已經比完了。
    /// 等權的代價是一個大庫在最後拖一段，所以預估時間由 <see cref="SearchEta"/> 照實際速率算，
    /// 而不是照這個數字線性外推一次就定案。
    /// </remarks>
    public double Fraction { get; }

    /// <summary>已經知道有幾個目標；還不知道時畫面畫不確定的進度。</summary>
    public bool IsDeterminate => Targets.Count > 0;
}
