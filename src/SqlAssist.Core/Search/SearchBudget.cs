using System;

namespace SqlAssist.Core.Search;

/// <summary>
/// 一輪搜尋的上限；用盡就停，並把結果標記為部分。
/// </summary>
/// <remarks>
/// 沿用 SQL Memory 搜尋那一套的精神：上限落在讀取迴圈上，不落在查詢條件裡，
/// 而且用盡時回傳已命中的部分並標記 <see cref="SearchResults.IsPartial"/>，
/// 不是回傳空的或悄悄截斷。沒有上限的話，延遲上界不存在——比不中任何東西的樣式
/// 剛好是最貴的那一種，因為它會把每一個候選都掃完。
///
/// 筆數上限拆成「每個 provider」與「排名後的總數」兩個，是為了可重現：
/// 一個所有 provider 共用的計數器，誰先被砍取決於誰先排到執行緒，
/// 同一組輸入在不同次執行會排出不同清單。每個來源各有自己的額度，
/// 砍在哪裡就只由那個來源自己的順序決定；總數上限則在排完序之後才裁，
/// 裁掉的一定是排在後面的。
/// </remarks>
public sealed class SearchBudget
{
    public const int DefaultMaxHits = 200;
    public const int DefaultMaxHitsPerProvider = 100;
    public const int DefaultMaxCandidatesPerProvider = 20000;

    /// <summary>打字驅動的一輪要在下一次按鍵之前結束，因此以百毫秒計而不是秒。</summary>
    public static readonly TimeSpan DefaultMaxDuration = TimeSpan.FromMilliseconds(400);

    public static SearchBudget Default { get; } = new();

    public SearchBudget(
        int maxHits = DefaultMaxHits,
        int maxHitsPerProvider = DefaultMaxHitsPerProvider,
        int maxCandidatesPerProvider = DefaultMaxCandidatesPerProvider,
        TimeSpan? maxDuration = null)
    {
        if (maxHits < 1) throw new ArgumentOutOfRangeException(nameof(maxHits));
        if (maxHitsPerProvider < 1) throw new ArgumentOutOfRangeException(nameof(maxHitsPerProvider));
        if (maxCandidatesPerProvider < 1) throw new ArgumentOutOfRangeException(nameof(maxCandidatesPerProvider));

        var duration = maxDuration ?? DefaultMaxDuration;
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxDuration));

        MaxHits = maxHits;
        MaxHitsPerProvider = maxHitsPerProvider;
        MaxCandidatesPerProvider = maxCandidatesPerProvider;
        MaxDuration = duration;
    }

    /// <summary>排名與去重之後保留幾筆；裁掉任何一筆都算部分結果。</summary>
    public int MaxHits { get; }

    /// <summary>單一 provider 最多收幾筆，超過就對它回 false。</summary>
    public int MaxHitsPerProvider { get; }

    /// <summary>單一 provider 最多檢查幾個候選，由 <see cref="ISearchSink.ReportExamined"/> 累計。</summary>
    public int MaxCandidatesPerProvider { get; }

    /// <summary>
    /// 這一輪的牆鐘上限，所有 provider 共用。
    /// </summary>
    /// <remarks>
    /// 這是合作式的：只在 provider 回報時才檢查，因此掛住不回報的 provider 不會被它中斷。
    /// 真的需要硬性期限時由呼叫端用 <see cref="System.Threading.CancellationTokenSource.CancelAfter(TimeSpan)"/>。
    /// 在聚合器裡另外開計時器的作法被放掉了：那會讓每一輪搜尋都配置一個計時器與連結權杖，
    /// 而絕大多數輪次在幾毫秒內就結束了。
    /// </remarks>
    public TimeSpan MaxDuration { get; }
}
