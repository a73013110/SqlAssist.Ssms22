using System;
using System.Linq;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlMemoryBrowserModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 30, 0, TimeSpan.Zero);

    private static SqlMemoryBrowserModel Ready()
    {
        var model = new SqlMemoryBrowserModel();
        Assert.True(model.ObserveHost(true, 1));
        model.Invalidate(Now);
        return model;
    }

    private static SqlMemoryPage<SqlHistoryItem> HistoryPage(string? cursor, bool partial = false, DateTimeOffset? through = null) =>
        partial ? new(Array.Empty<SqlHistoryItem>(), cursor!, through) : new(Array.Empty<SqlHistoryItem>(), cursor);

    [Fact]
    public void OptionListsMapPositionsToValuesInsteadOfCastingIndexes()
    {
        Assert.Equal(new[] { SqlHistoryFilter.All, SqlHistoryFilter.Executions, SqlHistoryFilter.Drafts },
            SqlMemoryBrowserModel.KindOptions.Select(option => option.Value));
        Assert.Equal(Enum.GetValues(typeof(SqlHistoryPeriod)).Length, SqlMemoryBrowserModel.PeriodOptions.Count);
        Assert.Equal(Enum.GetValues(typeof(SqlFavoriteScope)).Length, SqlMemoryBrowserModel.ScopeOptions.Count);
        Assert.Equal(Enum.GetValues(typeof(SqlConnectionFacetSort)).Cast<SqlConnectionFacetSort>(),
            SqlMemoryBrowserModel.SortOptions.Select(option => option.Value));
        Assert.Equal("A–Z", SqlMemoryBrowserModel.SortOptions.Single(option => option.Value == SqlConnectionFacetSort.Alphabetical).ShortLabel);
    }

    [Fact]
    public void HistoryFiltersBecomeOneRequestAndThePeriodIsFixedForTheWholePaginationRound()
    {
        var model = Ready();
        model.Kind = SqlHistoryFilter.Drafts;
        model.Search = "Loan";
        model.Server = "LibraryServer";
        model.Database = "Library";
        model.Period = SqlHistoryPeriod.ThirtyDays;
        model.Invalidate(Now);

        var first = model.BeginLoad()!;
        Assert.Null(first.Favorites);
        var request = first.History!;
        Assert.Equal((SqlMemoryBrowserModel.PageSize, SqlHistoryFilter.Drafts, "Loan", "LibraryServer", "Library"),
            (request.PageSize, request.Kind, request.Search, request.Server, request.Database));
        Assert.Equal(Now.AddDays(-30), request.Since);
        Assert.Null(model.BeginLoad());
        Assert.True(model.Accept(first, HistoryPage("next")));
        model.End(first);

        // 同一輪的下一頁沿用同一個起點與游標；時間也是游標指紋的一部分。
        var second = model.BeginLoad()!;
        Assert.Equal("next", second.History!.Cursor);
        Assert.Equal(Now.AddDays(-30), second.History.Since);
    }

    [Theory]
    [InlineData(SqlHistoryPeriod.SevenDays, -7)]
    [InlineData(SqlHistoryPeriod.Any, null)]
    public void PeriodsStartRelativeToTheInvalidationTime(SqlHistoryPeriod period, int? days)
    {
        var model = Ready();
        model.Period = period;
        model.Invalidate(Now);
        Assert.Equal(days is { } offset ? Now.AddDays(offset) : null, model.Since);
    }

    [Fact]
    public void TodayStartsAtLocalMidnight()
    {
        var model = Ready();
        model.Period = SqlHistoryPeriod.Today;
        model.Invalidate(Now);
        Assert.Equal(Now.ToLocalTime().Date, model.Since!.Value.DateTime);
    }

    [Fact]
    public void FavoritesNeedTheWholeScopeBeforeLoadingAndNeverCarryHistoryOnlyFilters()
    {
        var model = Ready();
        model.Tab = SqlMemoryBrowserTab.Favorites;
        model.Scope = SqlFavoriteScope.Database;
        model.Server = "LibraryServer";
        Assert.True(model.ShowsServerFilter && model.ShowsDatabaseFilter);
        Assert.Equal("請選擇", model.EmptyConnectionLabel);

        var blocked = model.BeginLoad()!;
        Assert.NotNull(blocked.BlockedMessage);
        Assert.False(model.IsLoading);

        model.Database = "Library";
        var load = model.BeginLoad()!;
        var request = load.Favorites!;
        Assert.Equal((SqlFavoriteScope.Database, "LibraryServer", "Library"), (request.Scope, request.Server, request.Database));

        model.End(load);
        model.Scope = SqlFavoriteScope.Global;
        model.Invalidate(Now);
        Assert.False(model.ShowsServerFilter);
        var global = model.BeginLoad()!.Favorites!;
        Assert.Null(global.Server);
        Assert.Null(global.Database);
    }

    /// <summary>取消撤不回已派送的隔離呼叫；舊篩選或舊儲存的回應都不得寫進目前清單。</summary>
    [Fact]
    public void LateResponsesFromAnOlderFilterOrHostGenerationAreRejected()
    {
        var model = Ready();
        var stale = model.BeginLoad()!;
        model.Invalidate(Now);
        Assert.False(model.Accept(stale, HistoryPage("old")));
        Assert.False(model.IsCurrent(stale));
        model.End(stale);

        var current = model.BeginLoad()!;
        Assert.True(model.ObserveHost(true, 2));
        Assert.False(model.Accept(current, HistoryPage("old-host")));
        Assert.False(model.ObserveHost(true, 2));
        Assert.True(model.ObserveHost(false, 2));
        Assert.Null(model.BeginLoad());
        Assert.False(model.CanLoadMore);
    }

    [Fact]
    public void APartialSearchPageTurnsLoadMoreIntoContinueSearchAndIsNotReportedAsEmpty()
    {
        var model = Ready();
        model.Search = "Loan";
        var load = model.BeginLoad()!;
        var through = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.True(model.Accept(load, HistoryPage("resume", partial: true, through: through)));
        model.End(load);

        Assert.True(model.CanLoadMore);
        Assert.False(model.CanAutoLoadMore);
        var footer = model.Footer(0);
        Assert.Equal((SqlMemoryFooterKind.ContinueSearch, "繼續搜尋", "符合 0 筆"), (footer.Kind, footer.ActionLabel, footer.Summary));
        Assert.StartsWith("已搜尋至 " + through.ToLocalTime().ToString("yyyy/MM/dd"), footer.Hint);
        model.Invalidate(Now);
        Assert.Null(model.SearchProgress);
        Assert.Equal(SqlMemoryFooterKind.Hidden, model.Footer(0).Kind);
        var empty = model.BeginLoad()!;
        Assert.True(model.Accept(empty, HistoryPage(null)));
        Assert.Equal(("沒有符合條件的項目", "可清除搜尋或放寬期間與範圍"), (model.Footer(0).Summary, model.Footer(0).Hint));
    }

    /// <summary>頁尾狀態：第一頁交給表面載入圖示；續頁進度留在頁尾原地；載完才說「全部」。</summary>
    [Fact]
    public void FooterFollowsPaginationWithoutFlashingEmptyBeforeTheFirstPage()
    {
        var model = Ready();
        var first = model.BeginLoad()!;
        Assert.Equal(SqlMemoryFooterKind.Hidden, model.Footer(0).Kind);
        Assert.True(model.Accept(first, HistoryPage("next")));
        model.End(first);

        var more = model.Footer(50);
        Assert.Equal((SqlMemoryFooterKind.More, "已載入 50 筆", "載入更多", true), (more.Kind, more.Summary, more.ActionLabel, more.CanAct));
        Assert.True(model.CanAutoLoadMore);

        var second = model.BeginLoad()!;
        var loading = model.Footer(50);
        Assert.Equal((SqlMemoryFooterKind.Loading, "載入中…", false), (loading.Kind, loading.ActionLabel, loading.CanAct));
        Assert.True(model.Accept(second, HistoryPage(null)));
        model.End(second);

        var end = model.Footer(73);
        Assert.Equal((SqlMemoryFooterKind.End, "已顯示全部 73 筆", null), (end.Kind, end.Summary, end.ActionLabel));
        Assert.True(model.ObserveHost(false, 1));
        Assert.Equal(SqlMemoryFooterKind.Hidden, model.Footer(73).Kind);
    }

    [Fact]
    public void RemovingARowKeepsThePositionOrFallsBackToTheNewLastRow()
    {
        Assert.Equal(2, SqlMemoryBrowserModel.SelectionAfterRemoval(2, 5));
        Assert.Equal(3, SqlMemoryBrowserModel.SelectionAfterRemoval(4, 4));
        Assert.Null(SqlMemoryBrowserModel.SelectionAfterRemoval(0, 0));
    }

    [Fact]
    public void AnEditedFavoriteLeavesTheListWhenItsScopeNoLongerMatches()
    {
        var model = Ready();
        model.Tab = SqlMemoryBrowserTab.Favorites;
        model.Scope = SqlFavoriteScope.Database;
        model.Server = "LibraryServer";
        model.Database = "Library";
        var library = new SqlConnectionLabel("LibraryServer", "Library");
        var favorite = new SqlFavorite(Guid.NewGuid(), "借閱查詢", null, Guid.NewGuid(), SqlFavoriteScope.Database, library);

        Assert.True(model.MatchesFavoriteScope(favorite));
        Assert.False(model.MatchesFavoriteScope(favorite with { Connection = new SqlConnectionLabel("LibraryServer", "Archive") }));
        Assert.False(model.MatchesFavoriteScope(favorite with { Scope = SqlFavoriteScope.Global, Connection = null }));
        model.Tab = SqlMemoryBrowserTab.History;
        Assert.False(model.MatchesFavoriteScope(favorite));
    }

    [Fact]
    public void RefreshRestoresTheRememberedRowOrPreviewsTheFirstWithoutStealingAnExistingSelection()
    {
        var model = Ready();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        model.RememberSelection(ids[2]);
        Assert.Equal(2, model.ResolveSelection(ids, hasSelection: false));
        // 還原只用一次；之後的「載入更多」不會又把選取拉回去。
        Assert.Null(model.ResolveSelection(ids, hasSelection: true));

        model.RememberSelection(Guid.NewGuid());
        Assert.Equal(0, model.ResolveSelection(ids, hasSelection: false));
        Assert.Null(model.ResolveSelection(Array.Empty<Guid>(), hasSelection: false));
    }

    [Fact]
    public void UsingTheCurrentConnectionSetsBothFiltersAndSwitchesFavoritesToTheDatabaseScope()
    {
        var model = Ready();
        model.Tab = SqlMemoryBrowserTab.Favorites;
        model.Server = "ArchiveServer";

        Assert.NotNull(model.UseConnection(null));
        Assert.NotNull(model.UseConnection(new SqlConnectionLabel("LibraryServer", "")));
        Assert.Equal("ArchiveServer", model.Server);

        Assert.Null(model.UseConnection(new SqlConnectionLabel("LibraryServer", "Library")));
        Assert.Equal((SqlFavoriteScope.Database, "LibraryServer", "Library"), (model.Scope, model.Server, model.Database));
    }

    [Fact]
    public void OnlyTheLatestFacetRequestOfEachKindIsAccepted()
    {
        var model = Ready();
        model.Server = "LibraryServer";
        var servers = model.BeginFacet(false);
        var oldDatabases = model.BeginFacet(true);
        var databases = model.BeginFacet(true);

        Assert.True(model.IsCurrentFacet(false, servers, 1));
        Assert.False(model.IsCurrentFacet(true, oldDatabases, 1));
        Assert.True(model.IsCurrentFacet(true, databases, 1));
        Assert.False(model.IsCurrentFacet(true, databases, 2));

        var request = model.FacetRequest(true, SqlConnectionFacetSort.Oldest, 100);
        Assert.Equal(("LibraryServer", SqlConnectionFacetSort.Oldest, 100, false), (request.Server, request.Sort, request.Offset, request.IsFavorites));
        Assert.Null(model.FacetRequest(false, SqlConnectionFacetSort.Recent, 0).Server);
    }

    [Fact]
    public void FailureTextFollowsTheStorageClassification()
    {
        Assert.Equal("載入失敗：資料庫正被其他作業使用；稍後再試。", SqlMemoryTimeText.Failure("載入",
            new SqlMemoryStorageException(SqlMemoryStorageErrorKind.Busy, "database is locked")));
        Assert.Equal("載入失敗：清單已變更；請重新整理。", SqlMemoryTimeText.Failure("載入",
            new SqlMemoryStorageException(SqlMemoryStorageErrorKind.InvalidCursor, "游標失效")));
        Assert.Equal("複製未完成：已停用", SqlMemoryTimeText.Failure("複製",
            new SqlMemoryStorageException(SqlMemoryStorageErrorKind.Unavailable, "已停用")));
        Assert.Equal("開啟失敗：內容已不存在", SqlMemoryTimeText.Failure("開啟", new InvalidOperationException("內容已不存在")));
    }
}
