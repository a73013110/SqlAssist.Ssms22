using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Search;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// 會記帳的假 sink：收下幾筆、被推了幾次、宣告了哪些目標與它們的結局。
/// </summary>
/// <remarks>
/// 「provider 收到 false 之後有沒有真的停下來」只看結果筆數是分不出來的——聚合器
/// 會把多推的那幾筆靜靜丟掉，而畫面上一模一樣。分得出來的是 <see cref="Reports"/>：
/// 繼續掃的那一版會一路推到底，停下來的那一版停在拒收之後的第一次。
///
/// 整份上鎖，與 <see cref="ISearchSink"/> 的契約一致（目錄 provider 是每個資料庫一條執行緒）。
/// 不上鎖的症狀是多資料庫那幾條測試偶爾少一筆，而它看起來像是產品漏了結果。
/// </remarks>
internal sealed class RecordingSearchSink : ISearchSink
{
    private readonly object _gate = new();
    private readonly List<SearchHit> _hits = new();
    private readonly List<SearchTarget> _targets = new();
    private readonly int _acceptLimit;
    private int _reports;

    /// <param name="acceptLimit">收下幾筆之後開始回 false，模擬這一輪被取代。</param>
    internal RecordingSearchSink(int acceptLimit = int.MaxValue)
    {
        _acceptLimit = acceptLimit;
    }

    internal IReadOnlyList<SearchHit> Hits
    {
        get
        {
            lock (_gate) return _hits.ToArray();
        }
    }

    /// <summary>被推了幾次，含被拒絕的那幾次。</summary>
    internal int Reports
    {
        get
        {
            lock (_gate) return _reports;
        }
    }

    /// <summary>宣告過的目標，依宣告順序；狀態是當下的快照。</summary>
    internal IReadOnlyList<SearchTargetStatus> Targets
    {
        get
        {
            lock (_gate) return _targets.Select(target => target.Status).ToArray();
        }
    }

    /// <summary>名稱是 <paramref name="name"/> 的那一個目標。</summary>
    internal SearchTargetStatus Target(string name) => Targets.Single(target => target.Name == name);

    /// <summary>每一個目標都比完而且沒有漏；聚合器那一條同一個定義。</summary>
    internal bool IsComplete => Targets.All(target => target.IsComplete);

    public bool TryReport(SearchHit hit)
    {
        lock (_gate)
        {
            _reports++;

            if (_hits.Count >= _acceptLimit) return false;

            _hits.Add(hit);
            return true;
        }
    }

    public SearchTarget AddTarget(string name, SearchTargetKind kind)
    {
        var target = new SearchTarget("test", name, kind);
        lock (_gate) _targets.Add(target);
        return target;
    }
}
