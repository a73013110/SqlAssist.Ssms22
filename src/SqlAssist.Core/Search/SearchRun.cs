using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.Search;

/// <summary>
/// 正在跑的一輪：邊跑邊問進度與已經到的命中，跑完由 <see cref="Completion"/> 交出排好的結果。
/// </summary>
/// <remarks>
/// 第一次建索引以秒計（「全部資料庫」可以到分鐘），這段時間畫面要說得出走到哪裡、先列出已經
/// 找到的東西；等整輪結束才一次交出，使用者只看得到一個轉圈，分不出是慢還是卡住了。
///
/// 邊跑邊看的命中照<b>到達順序</b>給，不排名：排名要整份在手上才有意義，每一次都重排的話
/// 使用者正在看的那一列會一直跳走。整輪結束後由 <see cref="Completion"/> 的結果一次排好。
/// </remarks>
public sealed class SearchRun
{
    private readonly object _gate = new();
    private readonly List<SearchHit> _arrived = new();
    private readonly List<SearchTarget> _targets = new();
    private readonly SearchAggregator _owner;
    private readonly CancellationToken _cancellationToken;
    private Task<SearchResults>? _completion;

    internal SearchRun(SearchAggregator owner, SearchQuery query, CancellationToken cancellationToken)
    {
        _owner = owner;
        Query = query;
        _cancellationToken = cancellationToken;
    }

    public SearchQuery Query { get; }

    public long Generation => Query.Generation;

    /// <summary>整輪的結果；取消與過期都不擲出，見 <see cref="SearchAggregator.Start"/>。</summary>
    public Task<SearchResults> Completion => _completion ?? throw new InvalidOperationException();

    /// <summary>這一輪還算數：沒有被更新的一輪取代，也沒有被取消。</summary>
    internal bool IsLive => !_cancellationToken.IsCancellationRequested && _owner.IsCurrent(Query.Generation);

    /// <summary>這一刻的進度；每一個目標的狀態都是當下的快照。</summary>
    public SearchProgress Progress()
    {
        lock (_gate)
        {
            var targets = new SearchTargetStatus[_targets.Count];
            for (var index = 0; index < targets.Length; index++) targets[index] = _targets[index].Snapshot();
            return new SearchProgress(targets, _arrived.Count);
        }
    }

    /// <summary>
    /// 從第 <paramref name="start"/> 筆開始、之後到的命中，照到達順序。
    /// </summary>
    /// <remarks>
    /// 呼叫端記著自己拿到第幾筆，每次只拿新的；整份複製一次的話，幾萬筆命中的那一輪每秒要搬十次。
    /// </remarks>
    public IReadOnlyList<SearchHit> ArrivedSince(int start)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));

        lock (_gate)
        {
            if (start >= _arrived.Count) return Array.Empty<SearchHit>();
            return _arrived.GetRange(start, _arrived.Count - start);
        }
    }

    internal void Begin(Task<SearchResults> completion) => _completion = completion;

    internal void Arrive(SearchHit hit)
    {
        lock (_gate) _arrived.Add(hit);
    }

    internal void Track(SearchTarget target)
    {
        lock (_gate) _targets.Add(target);
    }
}
