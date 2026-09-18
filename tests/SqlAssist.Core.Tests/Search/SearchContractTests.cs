using System;
using System.Linq;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Search;
using Xunit;

namespace SqlAssist.Core.Tests.Search;

public sealed class SearchContractTests
{
    [Fact]
    public void 查詢帶著正規化樣板讓每個候選不必重算()
    {
        var query = new SearchQuery("Lib_Reader");

        Assert.Equal("Lib_Reader", query.Text);
        Assert.Equal(FuzzyMatcher.NormalizePattern("Lib_Reader"), query.NormalizedPattern);
        Assert.True(FuzzyMatcher.MatchNormalized(query.NormalizedPattern, "Lib_Reader").IsMatch);
    }

    [Fact]
    public void 沒有分類過濾時每一種分類都留()
    {
        var query = new SearchQuery("loan");

        Assert.False(query.HasCategoryFilter);
        Assert.True(query.MatchesCategory("catalog.table"));
        Assert.True(query.MatchesCategory("memory.history"));
    }

    [Fact]
    public void 分類過濾區分大小寫()
    {
        var query = new SearchQuery("loan", categories: new[] { "catalog.table", "catalog.table" });

        Assert.True(query.HasCategoryFilter);
        Assert.Equal(new[] { "catalog.table" }, query.Categories);
        Assert.True(query.MatchesCategory("catalog.table"));

        // 只差大小寫的 Id 是不同 provider 各自宣告的，不能當成同一個。
        Assert.False(query.MatchesCategory("Catalog.Table"));
    }

    [Fact]
    public void 空輸入仍是合法查詢()
    {
        var query = new SearchQuery(string.Empty);

        Assert.True(query.IsEmpty);
        Assert.Empty(query.NormalizedPattern);
        Assert.True(query.Scope.IsUnbounded);
        Assert.Equal(SearchOptions.None, query.Options);
    }

    [Fact]
    public void 負的世代不合法()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchQuery("loan", -1));
    }

    [Fact]
    public void 範圍原樣保留不解讀()
    {
        var scope = new SearchScope(new[] { "[192.0.2.10]" }, new[] { "LibArchive", "libarchive" });
        var query = new SearchQuery("loan", scope: scope, options: SearchOptions.MatchCasing | SearchOptions.WholeWord);

        Assert.Equal(new[] { "[192.0.2.10]" }, query.Scope.Servers);
        Assert.Equal(new[] { "LibArchive", "libarchive" }, query.Scope.Databases);
        Assert.False(query.Scope.IsUnbounded);
        Assert.True(query.Options.HasFlag(SearchOptions.MatchCasing));
        Assert.True(query.Options.HasFlag(SearchOptions.WholeWord));
    }

    [Fact]
    public void 命中的排序鍵取限定名稱沒有路徑時退回標題()
    {
        Assert.True(SqlObjectPath.TryParseName(new[] { "LibArchive", "dbo", "Loan" }, out var path));

        var qualified = new SearchHit("catalog", "catalog.table", SearchHitClass.Name, "Loan", "dbo.Loan", 10, path);
        var pathless = new SearchHit("snippets", "snippets.default", SearchHitClass.Name, "Loan 範本", "snip:loan", 10);

        Assert.Equal("LibArchive.dbo.Loan", qualified.SortKey);
        Assert.Equal("Loan 範本", pathless.SortKey);
        Assert.Null(pathless.Path);
        Assert.Empty(pathless.Snippet);
        Assert.Empty(pathless.SnippetSpans);
    }

    [Fact]
    public void 命中保留片段區段與啟動載體()
    {
        var payload = new object();
        var hit = new SearchHit("memory", "memory.history", SearchHitClass.Body, "Loan", "memory:1", 5,
            snippet: "SELECT * FROM Loan", snippetSpans: new[] { new MatchSpan(14, 4) }, activatePayload: payload);

        Assert.Same(payload, hit.ActivatePayload);
        Assert.Equal(new MatchSpan(14, 4), Assert.Single(hit.SnippetSpans));
    }

    [Theory]
    [InlineData("", "catalog.table", "資料表")]
    [InlineData("catalog", "", "資料表")]
    [InlineData("catalog", "catalog.table", "")]
    public void 分類的識別字不可為空(string providerId, string id, string displayName)
    {
        Assert.Throws<ArgumentException>(() => new SearchCategory(providerId, id, displayName));
    }

    [Fact]
    public void 預算的每一項都要是正數()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchBudget(maxHits: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchBudget(maxHitsPerProvider: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchBudget(maxCandidatesPerProvider: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchBudget(maxDuration: TimeSpan.Zero));
    }

    [Fact]
    public void 預設預算以百毫秒計()
    {
        Assert.Equal(SearchBudget.DefaultMaxHits, SearchBudget.Default.MaxHits);
        Assert.Equal(SearchBudget.DefaultMaxHitsPerProvider, SearchBudget.Default.MaxHitsPerProvider);
        Assert.Equal(SearchBudget.DefaultMaxCandidatesPerProvider, SearchBudget.Default.MaxCandidatesPerProvider);
        Assert.True(SearchBudget.Default.MaxDuration < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void 命中類別的順序就是分組順序()
    {
        // 排名直接拿列舉值比大小，插進中間會靜靜改掉分組順序。
        Assert.Equal(0, (int)SearchHitClass.Name);
        Assert.Equal(1, (int)SearchHitClass.Body);
        Assert.Equal(2, Enum.GetValues(typeof(SearchHitClass)).Cast<SearchHitClass>().Count());
    }
}
