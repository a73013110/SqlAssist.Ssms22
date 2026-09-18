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
    /// <param name="dedupeKey">
    /// 跨 provider 穩定的去重鍵。同一個東西被兩個 provider 找到時要寫出同一個字串
    /// （例如限定名稱），否則清單上會出現兩列一模一樣的結果。
    /// </param>
    /// <param name="score">provider 給的原始分數，越大越前面；跨 provider 可比是 provider 的責任。</param>
    /// <param name="path">限定名稱；provider 沒有路徑概念（片段、設定）時為 null。</param>
    /// <param name="snippetSpans"><paramref name="snippet"/> 裡要高亮的區段。</param>
    public SearchHit(
        string providerId,
        string categoryId,
        SearchHitClass hitClass,
        string title,
        string dedupeKey,
        int score,
        SqlObjectPath? path = null,
        string snippet = "",
        IReadOnlyList<MatchSpan>? snippetSpans = null,
        object? activatePayload = null)
    {
        ProviderId = SearchArgument.Identifier(providerId, nameof(providerId));
        CategoryId = SearchArgument.Identifier(categoryId, nameof(categoryId));
        Title = SearchArgument.Identifier(title, nameof(title));
        DedupeKey = SearchArgument.Identifier(dedupeKey, nameof(dedupeKey));
        HitClass = hitClass;
        Score = score;
        Path = path;
        Snippet = snippet ?? throw new ArgumentNullException(nameof(snippet));
        SnippetSpans = snippetSpans ?? Array.Empty<MatchSpan>();
        ActivatePayload = activatePayload;

        // 排序鍵在這裡算一次：同分的比較會在排序過程中被呼叫 O(n log n) 次，
        // 而 SqlObjectPath.ToString 每次都重組整條名稱。
        SortKey = path is null ? Title : path.ToString();
    }

    public string ProviderId { get; }

    /// <summary>對應 <see cref="SearchCategory.Id"/>。</summary>
    public string CategoryId { get; }

    public SearchHitClass HitClass { get; }

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

    /// <summary>跨 provider 穩定的去重鍵。</summary>
    public string DedupeKey { get; }

    /// <summary>
    /// 同分時的排序鍵：有限定名稱就用它，沒有就退回標題。
    /// </summary>
    /// <remarks>
    /// 同分不打破平手的話，同一組輸入在不同次執行會排出不同順序（provider 是併發的），
    /// 使用者看到的症狀是清單會自己跳。退回標題而不是空字串，是因為沒有路徑的 provider
    /// （片段、設定）整組都會併成同一個鍵，等於那一群又回到不決定。
    /// </remarks>
    public string SortKey { get; }

    public override string ToString() => $"{ProviderId}:{CategoryId}:{HitClass}:{SortKey}({Score})";
}
