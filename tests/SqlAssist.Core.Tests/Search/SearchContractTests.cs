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
        Assert.Equal(SearchTargets.All, query.Targets);
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

    /// <summary>
    /// 命中部位是與分類獨立的第二條軸，而且 provider 據它跳過掃描。
    /// </summary>
    /// <remarks>
    /// 兩條軸混在一起的症狀是「資料行」變成一種物件種類，而使用者勾「只看資料表」時，
    /// 資料表上的資料行命中整組消失。
    /// </remarks>
    [Fact]
    public void 命中部位是獨立的一條軸()
    {
        var query = new SearchQuery("loan", targets: SearchTargets.Name | SearchTargets.Column);

        Assert.True(query.IncludesTarget(SearchMatchTarget.Name));
        Assert.True(query.IncludesTarget(SearchMatchTarget.Column));
        Assert.False(query.IncludesTarget(SearchMatchTarget.Text));

        // 分類過濾與部位過濾互不影響。
        Assert.True(query.MatchesCategory("catalog.table"));
    }

    [Theory]
    [InlineData(SearchTargets.None)]
    [InlineData((SearchTargets)16)]
    public void 掃不到任何部位的查詢不合法(SearchTargets targets)
    {
        // 一個部位都不掃的查詢一定是呼叫端算錯了：它不會找到任何東西，而畫面上與
        // 「這個字串不存在」一模一樣。
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchQuery("loan", targets: targets));
    }

    [Theory]
    [InlineData(SearchMatchTarget.Name, SearchTargets.Name)]
    [InlineData(SearchMatchTarget.Text, SearchTargets.Text)]
    [InlineData(SearchMatchTarget.Column, SearchTargets.Column)]
    public void 部位與旗標一一對應(SearchMatchTarget target, SearchTargets flag)
    {
        Assert.Equal(flag, target.ToFlag());
        Assert.True(new SearchQuery("loan", targets: flag).IncludesTarget(target));
    }

    /// <summary>
    /// 分組順序是名稱、資料行、定義本文，不是列舉值的順序。
    /// </summary>
    /// <remarks>
    /// <see cref="SearchMatchTarget.Column"/> 是後來從物件種類那條軸搬過來的，接在列舉最後
    /// 才不會改掉既有的值；但它在畫面上屬於名稱那一族——使用者打 <c>CopyNo</c> 要的是
    /// 「叫這個名字的資料行在哪幾張表」，而定義本文裡剛好也提到這幾個字的那一堆模組
    /// 排在它後面。照列舉值排的話，本文命中會插在名稱與資料行中間。
    /// </remarks>
    [Fact]
    public void 分組順序是名稱資料行本文()
    {
        Assert.Equal(
            new[] { SearchMatchTarget.Name, SearchMatchTarget.Column, SearchMatchTarget.Text },
            Enum.GetValues(typeof(SearchMatchTarget))
                .Cast<SearchMatchTarget>()
                .OrderBy(target => target.GroupOrder()));

        // 列舉值本身不得更動：SearchTargets 的旗標由它推出來。
        Assert.Equal(0, (int)SearchMatchTarget.Name);
        Assert.Equal(1, (int)SearchMatchTarget.Text);
        Assert.Equal(2, (int)SearchMatchTarget.Column);
        Assert.Equal(3, Enum.GetValues(typeof(SearchMatchTarget)).Length);
    }

    [Fact]
    public void 命中的排序鍵取限定名稱沒有路徑時退回標題()
    {
        Assert.True(SqlObjectPath.TryParseName(new[] { "LibArchive", "dbo", "Loan" }, out var path));

        var qualified = new SearchHit("catalog", "catalog.table", SearchMatchTarget.Name, "Loan", "dbo.Loan", 10, path);
        var pathless = new SearchHit("snippets", "snippets.default", SearchMatchTarget.Name, "Loan 範本", "snip:loan", 10);

        Assert.Equal("LibArchive.dbo.Loan", qualified.SortKey);
        Assert.Equal("Loan 範本", pathless.SortKey);
        Assert.Null(pathless.Path);
        Assert.Empty(pathless.Snippet);
        Assert.Empty(pathless.SnippetSpans);
        Assert.Empty(pathless.Badges);
    }

    [Fact]
    public void 命中保留片段區段與啟動載體()
    {
        var payload = new object();
        var hit = new SearchHit("memory", "memory.history", SearchMatchTarget.Text, "Loan", "memory:1", 5,
            snippet: "SELECT * FROM Loan", snippetSpans: new[] { new MatchSpan(14, 4) }, activatePayload: payload);

        Assert.Same(payload, hit.ActivatePayload);
        Assert.Equal(new MatchSpan(14, 4), Assert.Single(hit.SnippetSpans));
    }

    /// <summary>
    /// 膠囊由 provider 給，圖示是中性代號。
    /// </summary>
    /// <remarks>
    /// Core 認識 SSMS 的圖示型別等於把 VS 組件拉進 netstandard2.0 那一層；代號換成圖示
    /// 是 Ssms22 的事，認不得的代號那一層就不畫圖示，膠囊上的字照常出現。
    /// </remarks>
    [Fact]
    public void 命中帶得走provider給的膠囊()
    {
        var hit = new SearchHit(
            "catalog", "catalog.table", SearchMatchTarget.Name, "Loan", "dbo.Loan", 10,
            badges: new[]
            {
                new SearchBadge("LibArchive", SearchBadge.DatabaseIcon),
                new SearchBadge("已收藏"),
            });

        Assert.Equal(new[] { "LibArchive", "已收藏" }, hit.Badges.Select(badge => badge.Text));
        Assert.Equal("database", hit.Badges[0].IconToken);
        Assert.Null(hit.Badges[1].IconToken);
    }

    [Fact]
    public void 膠囊的字不可為空而代號不可為空字串()
    {
        Assert.Throws<ArgumentException>(() => new SearchBadge(""));
        Assert.Throws<ArgumentNullException>(() => new SearchBadge(null!));

        // 空字串的代號一定是呼叫端算出來的，而算出空字串的地方通常也算錯了別的東西。
        Assert.Throws<ArgumentException>(() => new SearchBadge("LibArchive", ""));
    }

    [Theory]
    [InlineData("", "catalog.table", "資料表")]
    [InlineData("catalog", "", "資料表")]
    [InlineData("catalog", "catalog.table", "")]
    public void 分類的識別字不可為空(string providerId, string id, string displayName)
    {
        Assert.Throws<ArgumentException>(() => new SearchCategory(providerId, id, displayName));
    }

    /// <summary>
    /// 分類帶著排序與分群；沒給分群時自成一群。
    /// </summary>
    /// <remarks>
    /// 只靠宣告順序的話，「其他」這種收納桶會被之後加進來的物件種類往前推，
    /// 而使用者每一次更新都要重新找那顆 pill 在哪裡。
    /// </remarks>
    [Fact]
    public void 分類帶著排序與分群()
    {
        var table = new SearchCategory("catalog", "catalog.table", "資料表", 0, "catalog.objects");
        var other = new SearchCategory("catalog", "catalog.other", "其他", 1000);

        Assert.Equal(0, table.SortOrder);
        Assert.Equal("catalog.objects", table.GroupId);

        // 沒給分群就自成一群，而不是與別人混在一起。
        Assert.Equal(1000, other.SortOrder);
        Assert.Equal("catalog.other", other.GroupId);
    }

    [Fact]
    public void 分群的識別字不可為空字串()
    {
        Assert.Throws<ArgumentException>(
            () => new SearchCategory("catalog", "catalog.table", "資料表", 0, ""));
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
}
