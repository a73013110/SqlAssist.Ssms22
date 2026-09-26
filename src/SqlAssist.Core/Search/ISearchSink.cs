namespace SqlAssist.Core.Search;

/// <summary>
/// provider 把結果串流推進來、並說出自己搜了哪些目標的地方。
/// </summary>
/// <remarks>
/// 不回傳 <c>List</c> 而走 sink，是讓畫面在最慢的來源回來之前就看得到結果與進度：
/// 第一次建索引以秒計，而名稱命中第一毫秒就算得出來。
///
/// <b>沒有預算。</b>每一個目標都要掃完，唯一的停止訊號是這一輪已經被取代或取消
/// （<see cref="TryReport"/> 回 false，或 <c>CancellationToken</c>）。延遲由取消控制：
/// 使用者多打一個字，這一輪就停。
///
/// 實作必須可以被多執行緒呼叫：同一個 provider 可以把目標拆成幾份平行掃。
/// </remarks>
public interface ISearchSink
{
    /// <summary>
    /// 推一筆結果。回傳 false 表示這一輪已經被取代或取消，provider 應該立刻停止。
    /// </summary>
    /// <remarks>
    /// 回傳 true 不保證這一筆會出現在最後的清單上：不在這一輪分類過濾範圍內的結果會被
    /// 靜靜丟掉，但那不是「該停了」，所以仍然回 true。
    /// </remarks>
    bool TryReport(SearchHit hit);

    /// <summary>
    /// 宣告這一輪要搜的一個目標；進度與結局都回報在傳回的那一份上。
    /// </summary>
    /// <remarks>
    /// 取資料之前就宣告：畫面靠目標數畫出「12／40 個資料庫」，晚宣告的目標會讓進度倒退。
    /// 被分類過濾整個排除的來源不宣告目標——它這一輪本來就不必搜，算進去會多一句
    /// 「SQL Agent 作業：已搜尋」而使用者明明勾掉了它。
    /// </remarks>
    SearchTarget AddTarget(string name, SearchTargetKind kind);
}
