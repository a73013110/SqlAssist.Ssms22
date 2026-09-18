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
/// </remarks>
internal sealed class RecordingSearchSink : ISearchSink
{
    private readonly int _acceptLimit;

    /// <param name="acceptLimit">收下幾筆之後開始回 false。</param>
    internal RecordingSearchSink(int acceptLimit = int.MaxValue)
    {
        _acceptLimit = acceptLimit;
    }

    internal List<SearchHit> Hits { get; } = new();

    /// <summary>被推了幾次，含被拒絕的那幾次。</summary>
    internal int Reports { get; private set; }

    /// <summary>provider 自己回報的候選檢查數總和。</summary>
    internal int Examined { get; private set; }

    /// <summary><see cref="ISearchSink.ReportExamined"/> 被呼叫幾次。</summary>
    internal int ExamineCalls { get; private set; }

    internal bool IsTruncated { get; private set; }

    internal string? Checkpoint { get; private set; }

    public bool IsExhausted => Hits.Count >= _acceptLimit;

    public bool TryReport(SearchHit hit)
    {
        Reports++;

        if (IsExhausted)
        {
            return false;
        }

        Hits.Add(hit);
        return true;
    }

    public void ReportExamined(int candidates)
    {
        ExamineCalls++;
        Examined += candidates;
    }

    public void ReportTruncated(string? checkpoint = null)
    {
        IsTruncated = true;
        Checkpoint = checkpoint;
    }
}
