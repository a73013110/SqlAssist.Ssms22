using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Search;

namespace SqlAssist.Core.Tests.Search;

/// <summary>
/// 測試用的來源：不連資料庫、不碰檔案，行為由委派決定。
/// </summary>
/// <remarks>
/// <see cref="Accepted"/> 記下每一次 <see cref="ISearchSink.TryReport"/> 的回傳值，
/// 「provider 有沒有收到停止訊號」才測得出來——只看最後的結果數，
/// 分不出是聚合器丟掉了還是 provider 根本沒被叫停。
/// </remarks>
internal sealed class FakeSearchProvider : ISearchProvider
{
    private readonly Func<SearchQuery, ISearchSink, CancellationToken, Task> _search;

    internal FakeSearchProvider(
        string id,
        Func<SearchQuery, ISearchSink, CancellationToken, Task> search,
        params SearchCategory[] categories)
    {
        Id = id;
        DisplayName = id;
        _search = search;
        Categories = Declare(id, categories);
    }

    /// <summary>照順序推完給定的結果，收到停止訊號就不再往下推。</summary>
    internal FakeSearchProvider(string id, params SearchHit[] hits)
    {
        Id = id;
        DisplayName = id;
        Categories = Declare(id, Array.Empty<SearchCategory>());
        _search = async (query, sink, cancellationToken) =>
        {
            // 讓每個來源都真的跨過一次排程，「順序可重現」才不是「剛好都同步跑完」的假象。
            await Task.Yield();

            foreach (var hit in hits)
            {
                var accepted = sink.TryReport(hit);
                Accepted.Add(accepted);
                if (!accepted) return;
            }
        };
    }

    public string Id { get; }

    public string DisplayName { get; }

    public IReadOnlyList<SearchCategory> Categories { get; }

    /// <summary>每一次推結果時 sink 的回應，依推的順序。</summary>
    internal List<bool> Accepted { get; } = new();

    public Task SearchAsync(SearchQuery query, ISearchSink sink, CancellationToken cancellationToken) =>
        _search(query, sink, cancellationToken);

    private static IReadOnlyList<SearchCategory> Declare(string id, SearchCategory[] categories) =>
        categories.Length == 0 ? new[] { new SearchCategory(id, id + ".default", id) } : categories;
}
