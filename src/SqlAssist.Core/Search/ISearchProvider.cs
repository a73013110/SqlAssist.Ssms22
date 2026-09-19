using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.Search;

/// <summary>
/// 一個搜尋來源：目錄物件、SQL Memory、開啟中的查詢分頁、片段、設定。
/// </summary>
/// <remarks>
/// 走 sink 加 <see cref="Task"/> 而不是回傳 <c>IAsyncEnumerable</c>，是因為 Core 是
/// netstandard2.0，非同步序列要多拉一個套件進這一層。
///
/// 實作不得依賴 UI 執行緒，也不得把例外當成控制流程往外丟：
/// <see cref="SearchAggregator"/> 會接住例外並隔離，但那一輪這個來源就沒有結果了。
/// </remarks>
public interface ISearchProvider
{
    /// <summary>跨版本穩定的識別字；與 <see cref="SearchHit.ProviderId"/> 一致。</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>這個來源會回報哪幾種分類；UI 靠它產生過濾 pill。內容固定，不隨查詢改變。</summary>
    IReadOnlyList<SearchCategory> Categories { get; }

    /// <summary>
    /// 掃描並把結果推進 <paramref name="sink"/>。
    /// </summary>
    /// <remarks>
    /// <paramref name="sink"/> 的 <see cref="ISearchSink.TryReport"/> 回 false 時必須停止；
    /// 取消同理。回報的 <see cref="SearchHit.CategoryId"/> 必須出自
    /// <see cref="Categories"/>，而且應該自己先套用 <see cref="SearchQuery.Categories"/> 過濾
    /// ——聚合器那一道只是最後防線，靠它過濾等於把不要的候選也算進預算。
    ///
    /// <see cref="SearchQuery.Targets"/> 要在<b>取資料之前</b>問，不是取回來再濾：
    /// 這個旗標存在的唯一理由就是讓不要的那一段真的不必付代價，而聚合器沒有辦法
    /// 替 provider 省掉一次全表掃描。
    /// </remarks>
    Task SearchAsync(SearchQuery query, ISearchSink sink, CancellationToken cancellationToken);
}
