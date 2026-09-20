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

    /// <summary>
    /// 這一輪有東西根本沒開始掃，因為讀不到。
    /// </summary>
    /// <param name="reason">
    /// 給人看的一句話，由 provider 自己寫；Core 不解讀、不翻譯，也不加框。
    /// 呼叫端只會把它原樣貼在狀態列上，所以要說得出「少了什麼」與「多半是為什麼」。
    /// 空字串會擲出：說不出原因的「讀不到」與泛用的「部分結果」在畫面上一模一樣，
    /// 而那正是這個方法存在的理由。
    /// </param>
    /// <param name="kind">
    /// 結構化的原因，給呈現那一層決定抬頭用；<paramref name="reason"/> 是給人看的那一句，
    /// 兩者都要。只有 provider 真的分得出「就是權限」時才給
    /// <see cref="SearchUnavailableKind.Denied"/>——猜錯的那一次會叫使用者去查一個
    /// 好好的權限設定。分不出來時留 <see cref="SearchUnavailableKind.Unknown"/>，
    /// 那只是少說一句話。
    /// </param>
    /// <remarks>
    /// 與 <see cref="ReportTruncated(string?)"/> 是兩件事，而且畫面上要說的話完全相反：
    /// 「沒掃完」叫使用者縮小範圍或加長關鍵字，「讀不到」叫他去看權限。混成一件的症狀是
    /// 使用者先照前一句試三次——而那一句對他的情況完全沒有用。
    ///
    /// 與「provider 擲例外」也是兩件事。例外走
    /// <see cref="SearchProviderFailure"/>，呼叫端會把它當成錯誤報出來；對一個本來就
    /// 多半讀不到的來源（多數登入對 <c>msdb</c> 沒有 <c>SELECT</c>），那等於每一次搜尋
    /// 都在報錯。
    ///
    /// 這個回報<b>不</b>讓 sink 進入 <see cref="IsExhausted"/>，也不算截斷：一個 provider
    /// 可以同時跨好幾個目標（目錄那一邊是每個資料庫一條執行緒），其中一個讀不到不該
    /// 讓其他幾個停下來。整輪仍然算部分結果——這一輪確實少了東西。
    ///
    /// 同一輪說第二次時留著第一句；後到的覆蓋先到的話，交出去的句子由賽跑決定，
    /// 同一組輸入每次說的話不一樣。要合併好幾個目標時由 provider 自己先組成一句。
    /// <paramref name="kind"/> 則相反：第二次說的種類不同時整個退回
    /// <see cref="SearchUnavailableKind.Unknown"/>。一個 provider 跨好幾個目標時，
    /// 「一個沒權限、一個連不上」的下一步不是「去要權限」，而留第一個說的那一版，
    /// 交出去的斷言由賽跑決定。
    /// </remarks>
    void ReportUnavailable(string reason, SearchUnavailableKind kind = SearchUnavailableKind.Unknown);
}
