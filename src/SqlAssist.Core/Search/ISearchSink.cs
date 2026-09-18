namespace SqlAssist.Core.Search;

/// <summary>
/// provider 把結果串流推進來的地方。
/// </summary>
/// <remarks>
/// 不回傳 <c>List</c> 而走 sink，是這個框架唯一的效能接縫：名稱命中在使用者還在打字時
/// 就要上畫面，定義本文命中慢慢補。provider 收集完才回傳的話，整輪的延遲等於最慢那一個
/// 來源，而名稱命中明明第一毫秒就算得出來。
///
/// 實作必須可以被多執行緒呼叫：同一個 provider 可以把候選拆成幾份平行掃。
/// </remarks>
public interface ISearchSink
{
    /// <summary>
    /// 預算已經用完，再推也不會被收。
    /// </summary>
    /// <remarks>
    /// 給「下一批候選很貴」的 provider 在取資料之前先問一次用的；
    /// 只靠 <see cref="TryReport"/> 的回傳值，代價是至少要先算出一筆才知道該停。
    /// </remarks>
    bool IsExhausted { get; }

    /// <summary>
    /// 推一筆結果。回傳 false 表示預算已滿或這一輪已經過期，provider 應該立刻停止掃描。
    /// </summary>
    /// <remarks>
    /// 回傳 true 不保證這一筆會出現在最後的清單上：不在這一輪分類過濾範圍內的結果會被
    /// 靜靜丟掉，但那不是「該停了」，所以仍然回 true。
    /// </remarks>
    bool TryReport(SearchHit hit);

    /// <summary>
    /// 回報這一輪又檢查過幾個候選（含沒命中的）。
    /// </summary>
    /// <remarks>
    /// 候選預算是給「掃了很多、命中很少」那種輸入用的：只算命中數的話，
    /// 一個比不中任何東西的樣式會把整個目錄掃完才回來，而且看起來像是零成本。
    /// 逐筆呼叫太吵，provider 每掃一批回報一次即可。
    /// </remarks>
    void ReportExamined(int candidates);

    /// <summary>
    /// 這一輪沒有掃完；結果是部分的。
    /// </summary>
    /// <param name="checkpoint">
    /// 掃到哪裡的不透明字串（例如最後檢查過的候選鍵），供呼叫端決定要不要往下找。
    /// Core 不解讀，也不會自動續搜——沿用 SQL Memory 搜尋的作法，
    /// 是否往前找由呼叫端決定。
    /// </param>
    void ReportTruncated(string? checkpoint = null);
}
