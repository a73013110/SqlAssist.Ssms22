using System;
using System.Collections.Generic;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Search;

/// <summary>
/// provider 回報的一筆結果；不可變。
/// </summary>
/// <remarks>
/// 這一層不知道結果是什麼東西，也不打算知道：<see cref="ActivatePayload"/> 原樣帶回 UI，
/// 分數由 provider 自己給。Core 只做三件事——分組、排序與去重——所以只有這三件事用得到的欄位
/// 有語意，其餘都是載體。
/// </remarks>
public sealed class SearchHit
{
    private static readonly SearchBadge[] NoBadges = Array.Empty<SearchBadge>();
    private static readonly SearchHit[] NoHits = Array.Empty<SearchHit>();

    /// <param name="matchTarget">
    /// 命中打在名稱、資料行還是定義本文上；與 <paramref name="categoryId"/> 是兩條獨立的軸。
    /// </param>
    /// <param name="dedupeKey">
    /// 跨 provider 穩定的去重鍵。同一個東西被兩個 provider 找到時要寫出同一個字串
    /// （例如限定名稱），否則清單上會出現兩列一模一樣的結果。同一個東西被同一個 provider
    /// 以不同部位命中時也是同一個鍵——那仍然是同一列，被併掉的那幾筆掛在
    /// <see cref="Merged"/> 上，見 <see cref="SearchAggregator"/> 的合併規則。
    /// </param>
    /// <param name="score">provider 給的原始分數，越大越前面；跨 provider 可比是 provider 的責任。</param>
    /// <param name="path">限定名稱；provider 沒有路徑概念（片段、設定）時為 null。</param>
    /// <param name="snippetSpans"><paramref name="snippet"/> 裡要高亮的區段。</param>
    /// <param name="badges">
    /// 顯示膠囊；沒有脈絡要說時留空。Core 不解讀也不比較，只原樣帶到 UI。
    /// </param>
    public SearchHit(
        string providerId,
        string categoryId,
        SearchMatchTarget matchTarget,
        string title,
        string dedupeKey,
        int score,
        SqlObjectPath? path = null,
        string snippet = "",
        IReadOnlyList<MatchSpan>? snippetSpans = null,
        object? activatePayload = null,
        IReadOnlyList<SearchBadge>? badges = null)
    {
        ProviderId = SearchArgument.Identifier(providerId, nameof(providerId));
        CategoryId = SearchArgument.Identifier(categoryId, nameof(categoryId));
        Title = SearchArgument.Identifier(title, nameof(title));
        DedupeKey = SearchArgument.Identifier(dedupeKey, nameof(dedupeKey));
        MatchTarget = matchTarget;
        Score = score;
        Path = path;
        Snippet = snippet ?? throw new ArgumentNullException(nameof(snippet));
        SnippetSpans = snippetSpans ?? Array.Empty<MatchSpan>();
        ActivatePayload = activatePayload;
        Badges = badges ?? NoBadges;
        Merged = NoHits;

        // 排序鍵在這裡算一次：同分的比較會在排序過程中被呼叫 O(n log n) 次，
        // 而 SqlObjectPath.ToString 每次都重組整條名稱。
        SortKey = path is null ? Title : path.ToString();
    }

    /// <summary>複製一份，換掉併進來的那幾筆；只有聚合器用得到。</summary>
    private SearchHit(SearchHit source, IReadOnlyList<SearchHit> merged)
    {
        ProviderId = source.ProviderId;
        CategoryId = source.CategoryId;
        Title = source.Title;
        DedupeKey = source.DedupeKey;
        MatchTarget = source.MatchTarget;
        Score = source.Score;
        Path = source.Path;
        Snippet = source.Snippet;
        SnippetSpans = source.SnippetSpans;
        ActivatePayload = source.ActivatePayload;
        Badges = source.Badges;
        SortKey = source.SortKey;
        Merged = merged;
    }

    public string ProviderId { get; }

    /// <summary>對應 <see cref="SearchCategory.Id"/>；講的是「這是哪一種東西」。</summary>
    public string CategoryId { get; }

    /// <summary>命中打在哪一個部位上；講的是「對上的是它的哪裡」。</summary>
    public SearchMatchTarget MatchTarget { get; }

    /// <summary>清單上那一列的字。</summary>
    public string Title { get; }

    /// <summary>限定名稱；沒有路徑概念時為 null。</summary>
    public SqlObjectPath? Path { get; }

    /// <summary>第二行的片段文字；沒有片段時是空字串。</summary>
    public string Snippet { get; }

    /// <summary>命中區段，索引落在 <see cref="Snippet"/> 上。</summary>
    public IReadOnlyList<MatchSpan> SnippetSpans { get; }

    public int Score { get; }

    /// <summary>UI 啟動這一筆結果要用的東西；Core 不解讀也不比較。</summary>
    public object? ActivatePayload { get; }

    /// <summary>provider 掛的顯示膠囊；沒有時是空的。</summary>
    public IReadOnlyList<SearchBadge> Badges { get; }

    /// <summary>跨 provider 穩定的去重鍵。</summary>
    public string DedupeKey { get; }

    /// <summary>
    /// 併進這一筆的其他命中：同一個去重鍵、排名較低的那幾筆，依排名排好。
    /// </summary>
    /// <remarks>
    /// 只有聚合器填得出來，provider 交出來的一律是空的。內容是<b>已經攤平</b>的——
    /// 併進來的那幾筆自己的 <see cref="Merged"/> 一定是空的，呼叫端不必遞迴。
    ///
    /// 留著它而不是丟掉，是因為「這一列為什麼在這裡」的答案分散在那幾筆上：一張資料表
    /// 可能是靠三個資料行命中的，只留排名最高的那一筆等於畫面上說「它有一個叫某某的資料行」，
    /// 而使用者接著會問另外兩個去哪了。
    /// </remarks>
    public IReadOnlyList<SearchHit> Merged { get; }

    /// <summary>這一列代表的所有命中：自己排第一，其餘依序跟在後面。</summary>
    /// <remarks>
    /// 呈現那一層要的幾乎都是這一份而不是 <see cref="Merged"/>：計數、命中部位與預覽要標的
    /// 位置講的都是「這一列總共對上了幾處」，漏掉自己那一筆的症狀是只被名稱命中的物件
    /// 顯示成沒有命中。
    /// </remarks>
    public IReadOnlyList<SearchHit> Matches
    {
        get
        {
            if (Merged.Count == 0) return new[] { this };

            var all = new SearchHit[Merged.Count + 1];
            all[0] = this;
            for (var index = 0; index < Merged.Count; index++) all[index + 1] = Merged[index];
            return all;
        }
    }

    /// <summary>把幾筆同鍵的命中併進來；聚合器去重時呼叫，其餘地方拿不到。</summary>
    internal SearchHit WithMerged(IReadOnlyList<SearchHit> merged) =>
        merged.Count == 0 ? this : new SearchHit(this, merged);

    /// <summary>
    /// 同分時的排序鍵：有限定名稱就用它，沒有就退回標題。
    /// </summary>
    /// <remarks>
    /// 同分不打破平手的話，同一組輸入在不同次執行會排出不同順序（provider 是併發的），
    /// 使用者看到的症狀是清單會自己跳。退回標題而不是空字串，是因為沒有路徑的 provider
    /// （片段、設定）整組都會併成同一個鍵，等於那一群又回到不決定。
    /// </remarks>
    public string SortKey { get; }

    public override string ToString() => $"{ProviderId}:{CategoryId}:{MatchTarget}:{SortKey}({Score})";
}
