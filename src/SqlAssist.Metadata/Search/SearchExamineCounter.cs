using SqlAssist.Core.Search;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 攢一批候選再向 sink 回報一次，並記住掃到哪裡。
/// </summary>
/// <remarks>
/// 逐筆呼叫 <see cref="ISearchSink.ReportExamined"/> 太吵——候選數是以萬計的，
/// 而 sink 每一次都要做一次預算判斷。攢一批再報，代價是最多超掃
/// <see cref="Batch"/> 個候選才發現預算用盡。
///
/// 續掃位置是不透明字串，Core 不解讀也不會自動續搜——是否往前找由呼叫端決定。
///
/// <b>不上鎖。</b>一個實例只由一條執行緒用（目錄那一邊是每個資料庫一份，
/// 作業那一邊整個來源只有一條路）。共用一份的話，這把鎖會落在最熱的迴圈裡。
/// </remarks>
internal sealed class SearchExamineCounter
{
    /// <summary>每檢查幾個候選回報一次。</summary>
    internal const int Batch = 64;

    private readonly ISearchSink _sink;
    private int _pending;

    internal SearchExamineCounter(ISearchSink sink) => _sink = sink;

    /// <summary>最後一個檢查過的候選；截斷時當續掃位置交出去。</summary>
    internal string? Checkpoint { get; private set; }

    internal void Note(string candidateKey)
    {
        Checkpoint = candidateKey;

        if (++_pending >= Batch)
        {
            Flush();
        }
    }

    internal void Flush()
    {
        if (_pending == 0)
        {
            return;
        }

        _sink.ReportExamined(_pending);
        _pending = 0;
    }
}
