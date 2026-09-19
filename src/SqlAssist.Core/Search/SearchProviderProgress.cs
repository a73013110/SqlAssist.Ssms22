namespace SqlAssist.Core.Search;

/// <summary>
/// 單一 provider 這一輪掃到哪裡。
/// </summary>
/// <remarks>
/// 「部分」在整份結果上只有一個布林，但要往下找的時候得知道是誰沒掃完、從哪裡接。
/// 只留總體布林的話，呼叫端唯一能做的事是整輪重來。
///
/// 「讀不到」也掛在這裡，不另外開一份清單：一份清單與這一份會各自記一次 provider Id，
/// 而兩邊對不上（其中一份少了一個來源）沒有任何一處看得出來。呼叫端本來就要走過這一份。
/// </remarks>
public sealed class SearchProviderProgress
{
    internal SearchProviderProgress(
        string providerId,
        int examined,
        int reported,
        bool isTruncated,
        string? checkpoint,
        string? unavailableReason = null)
    {
        ProviderId = providerId;
        Examined = examined;
        Reported = reported;
        IsTruncated = isTruncated;
        Checkpoint = checkpoint;
        UnavailableReason = unavailableReason;
    }

    public string ProviderId { get; }

    /// <summary>provider 自己回報的候選檢查數。</summary>
    public int Examined { get; }

    /// <summary>被這一輪收下的筆數（已扣掉分類過濾掉的）。</summary>
    public int Reported { get; }

    /// <summary>這個來源沒掃完：預算用盡、被取消，或它自己擲了例外。</summary>
    public bool IsTruncated { get; }

    /// <summary>provider 給的續掃位置；沒給時為 null。Core 不解讀。</summary>
    public string? Checkpoint { get; }

    /// <summary>
    /// 這一輪有東西根本沒開始掃，因為讀不到。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="IsTruncated"/> 獨立：讀不到的來源沒有「掃到哪裡」可言，
    /// 而一個掃到一半停下來的來源說的是另一句話。兩者可以同時為真——例如幾個資料庫
    /// 裡有一個連不上、另一個掃到預算用盡。
    /// </remarks>
    public bool IsUnavailable => UnavailableReason is not null;

    /// <summary>
    /// provider 給的那一句話，給人看的；沒有讀不到的東西時為 null。
    /// </summary>
    /// <remarks>
    /// Core 不解讀、不翻譯也不加框。呈現那一層因此不必認得任何一個特定 provider 的常數
    /// ——認得的那一版，每多一個「權限常常不足」的來源就要在同一處多一個 <c>if</c>。
    /// </remarks>
    public string? UnavailableReason { get; }

    public override string ToString() =>
        $"{ProviderId}: {Reported}/{Examined}{(IsTruncated ? " (部分)" : "")}{(IsUnavailable ? " (讀不到)" : "")}";
}
