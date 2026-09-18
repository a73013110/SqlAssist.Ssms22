namespace SqlAssist.Core.Search;

/// <summary>
/// 單一 provider 這一輪掃到哪裡。
/// </summary>
/// <remarks>
/// 「部分」在整份結果上只有一個布林，但要往下找的時候得知道是誰沒掃完、從哪裡接。
/// 只留總體布林的話，呼叫端唯一能做的事是整輪重來。
/// </remarks>
public sealed class SearchProviderProgress
{
    internal SearchProviderProgress(string providerId, int examined, int reported, bool isTruncated, string? checkpoint)
    {
        ProviderId = providerId;
        Examined = examined;
        Reported = reported;
        IsTruncated = isTruncated;
        Checkpoint = checkpoint;
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

    public override string ToString() => $"{ProviderId}: {Reported}/{Examined}{(IsTruncated ? " (部分)" : "")}";
}
