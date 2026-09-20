using System.Collections.Generic;
using SqlAssist.Core.Search;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// 會計數的假 sink：收下幾筆、檢查過幾個候選、有沒有被叫停。
/// </summary>
/// <remarks>
/// 「provider 收到 false 之後有沒有真的停下來」只看結果筆數是分不出來的——聚合器
/// 會把多推的那幾筆靜靜丟掉，而畫面上一模一樣。分得出來的是
/// <see cref="Examined"/>：繼續掃的那一版會一路數到底，停下來的那一版停在原地。
///
/// 整份上鎖，與 <see cref="ISearchSink"/> 的契約一致（provider 可以把候選拆成幾份平行掃，
/// 而目錄 provider 正是每個資料庫一條執行緒）。不上鎖的症狀是多資料庫那幾條測試
/// 偶爾少一筆，而它看起來像是產品漏了結果。
/// </remarks>
internal sealed class RecordingSearchSink : ISearchSink
{
    private readonly object _gate = new();
    private readonly List<SearchHit> _hits = new();
    private readonly int _acceptLimit;
    private int _reports;
    private int _examined;
    private int _examineCalls;
    private bool _truncated;
    private string? _checkpoint;
    private string? _unavailableReason;
    private SearchUnavailableKind _unavailableKind;

    /// <param name="acceptLimit">收下幾筆之後開始回 false。</param>
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

    /// <summary>provider 自己回報的候選檢查數總和。</summary>
    internal int Examined
    {
        get
        {
            lock (_gate) return _examined;
        }
    }

    /// <summary><see cref="ISearchSink.ReportExamined"/> 被呼叫幾次。</summary>
    internal int ExamineCalls
    {
        get
        {
            lock (_gate) return _examineCalls;
        }
    }

    internal bool IsTruncated
    {
        get
        {
            lock (_gate) return _truncated;
        }
    }

    internal string? Checkpoint
    {
        get
        {
            lock (_gate) return _checkpoint;
        }
    }

    /// <summary>有沒有收到「這一輪讀不到」；與 <see cref="IsTruncated"/> 是兩件事。</summary>
    internal bool IsUnavailable
    {
        get
        {
            lock (_gate) return _unavailableReason is not null;
        }
    }

    internal string? UnavailableReason
    {
        get
        {
            lock (_gate) return _unavailableReason;
        }
    }

    /// <summary>provider 回報的結構化原因；「權限不足」這個抬頭的唯一來源。</summary>
    internal SearchUnavailableKind UnavailableKind
    {
        get
        {
            lock (_gate) return _unavailableKind;
        }
    }

    public bool IsExhausted
    {
        get
        {
            lock (_gate) return _hits.Count >= _acceptLimit;
        }
    }

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

    public void ReportExamined(int candidates)
    {
        lock (_gate)
        {
            _examineCalls++;
            _examined += candidates;
        }
    }

    public void ReportTruncated(string? checkpoint = null)
    {
        lock (_gate)
        {
            _truncated = true;
            _checkpoint = checkpoint;
        }
    }

    /// <remarks>
    /// 留第一句而種類在不同時退回 <see cref="SearchUnavailableKind.Unknown"/>，
    /// 與 <c>SearchAggregator</c> 的 sink 同一條規則：句子後到的覆蓋先到的話，
    /// 多資料庫那幾條測試的期望值會由賽跑決定；種類留第一個說的話，
    /// 「一個沒權限、一個連不上」會被斷言成權限。
    /// </remarks>
    public void ReportUnavailable(string reason, SearchUnavailableKind kind = SearchUnavailableKind.Unknown)
    {
        lock (_gate)
        {
            if (_unavailableReason is null)
            {
                _unavailableReason = reason;
                _unavailableKind = kind;
            }
            else if (_unavailableKind != kind)
            {
                _unavailableKind = SearchUnavailableKind.Unknown;
            }
        }
    }
}
