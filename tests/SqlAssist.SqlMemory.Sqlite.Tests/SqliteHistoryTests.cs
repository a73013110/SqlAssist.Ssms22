using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.Matching;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

public sealed class SqliteHistoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Distinct(int sequence) => "SELECT * FROM Lib_Reader WHERE ReaderId=" + sequence + ";";

    [Fact]
    public async Task ConsecutiveIdenticalExecutionsMergeIntoOneHistoryRowButKeepEveryEvent()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        // 沒有連線也算相同連線：兩邊都是 NULL 時照樣合併。
        for (var i = 1; i <= 3; i++) await store.Process(repository, store.Capture(i, seconds: i), Token);

        var row = Assert.Single((await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Executions), Token)).Items);
        Assert.Equal(3, row.ExecutionCount);
        Assert.Equal(SqliteTestStore.Start.AddSeconds(1), row.FirstExecutedAt);
        Assert.Equal(SqliteTestStore.Start.AddSeconds(3), row.CreatedAt);
        Assert.Null(row.Connection);
        Assert.Equal(3L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(1L, store.Scalar("SELECT count(DISTINCT EntryKey) FROM Executions;"));
        Assert.Equal("e" + row.ItemId.ToString("N"), store.Scalar("SELECT LatestExecutionEntryKey FROM Sessions;"));
        // 草稿不帶次數與首次執行時間，schema 的 CHECK 也不允許。
        var draft = (await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Drafts), Token)).Items;
        Assert.All(draft, item => { Assert.Equal(1, item.ExecutionCount); Assert.Null(item.FirstExecutedAt); });
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task OnlyConsecutiveExecutionsWithTheSameContentAndConnectionInTheSameSessionMerge()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var library = new SqlConnectionLabel("LibraryServer", "Library");
        const string a = "SELECT * FROM Lib_Reader;", b = "SELECT * FROM Lib_Tag;";
        var other = new SqlSession(Guid.NewGuid(), store.Document.DocumentId);
        await store.Process(repository, store.Capture(1, a, seconds: 1, context: library), Token);
        await store.Process(repository, store.Capture(2, a, seconds: 2, context: library), Token);
        // 連線名稱只差大小寫也是另一個連線，與篩選的精確比對一致。
        await store.Process(repository, store.Capture(3, a, seconds: 3, context: new SqlConnectionLabel("LIBRARYSERVER", "Library")), Token);
        await store.Process(repository, store.Capture(4, a, seconds: 4, context: new SqlConnectionLabel("LIBRARYSERVER", "Archive")), Token);
        await store.Process(repository, store.Capture(5, b, seconds: 5, context: library), Token);
        await store.Process(repository, store.Capture(6, a, seconds: 6, context: library), Token);
        // 另一個視窗執行同一份 SQL 不合併；原視窗接著再執行，仍併回它自己的上一筆。
        await store.Process(repository, store.Capture(1, a, seconds: 7, context: library, session: other), Token);
        await store.Process(repository, store.Capture(7, a, seconds: 8, context: library), Token);

        var rows = (await repository.ReadHistoryAsync(new SqlHistoryRequest(20, SqlHistoryFilter.Executions), Token)).Items;
        Assert.Equal(new[] { 8, 7, 5, 4, 3, 2 }, rows.Select(row => (int)(row.CreatedAt - SqliteTestStore.Start).TotalSeconds));
        Assert.Equal(new[] { 2, 1, 1, 1, 1, 2 }, rows.Select(row => row.ExecutionCount));
        Assert.Equal(other.SessionId, rows[1].SessionId);
        Assert.Equal(SqliteTestStore.Start.AddSeconds(6), rows[0].FirstExecutedAt);
        Assert.Equal(SqliteTestStore.Start.AddSeconds(1), rows[5].FirstExecutedAt);
        Assert.Equal(8L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task DeletingAMergedRowRemovesEveryExecutionUnderIt()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        const string copy = "SELECT CopyNo FROM Cat_BookCopy;";
        await store.Process(repository, store.Capture(1, selected: copy, seconds: 1), Token);
        await store.Process(repository, store.Capture(2, selected: copy, seconds: 2), Token);
        await store.Process(repository, store.Capture(3, selected: "SELECT * FROM Loan;", seconds: 3), Token);
        var rows = (await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Executions), Token)).Items;
        var merged = Assert.Single(rows, row => row.ExecutionCount == 2);

        Assert.Equal(SqlHistoryDeleteResult.Deleted, await repository.DeleteHistoryAsync(merged, Token));

        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM History WHERE Kind=1;"));
        // 底下所有執行都刪掉後，選取版本失去最後的引用，連同內容一起回收。
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions WHERE IsExecutionSelection=1;"));
        Assert.Null(await repository.ReadContentAsync(merged.ContentId, Token));
        Assert.Equal(SqlHistoryDeleteResult.NotFound, await repository.DeleteHistoryAsync(merged, Token));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));

        // Session 記下的上一筆執行列被刪掉後，同一份 SQL 再執行要另起新列，不能復活舊鍵。
        var latest = Assert.Single((await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Executions), Token)).Items);
        Assert.Equal(SqlHistoryDeleteResult.Deleted, await repository.DeleteHistoryAsync(latest, Token));
        await store.Process(repository, store.Capture(4, selected: "SELECT * FROM Loan;", seconds: 4), Token);
        var again = Assert.Single((await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Executions), Token)).Items);
        Assert.Equal(1, again.ExecutionCount);
        Assert.NotEqual(latest.ItemId, again.ItemId);
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Executions;"));
    }

    [Fact]
    public async Task SameTimestampKeysetPagingHasNoMissingOrDuplicateEntries()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        // 每次內容不同：同 Session 連續執行同一份 SQL 會併成一列，這裡要的是 23 列同時間的投影。
        for (var i = 1; i <= 23; i++) await store.Process(repository, store.Capture(i, Distinct(i)), Token);
        var ids = new HashSet<Guid>();
        string? cursor = null;
        do
        {
            var page = await repository.ReadHistoryAsync(new SqlHistoryRequest(5, SqlHistoryFilter.Executions, cursor: cursor), Token);
            Assert.InRange(page.Items.Count, 1, 5);
            foreach (var item in page.Items) Assert.True(ids.Add(item.ItemId));
            cursor = page.NextCursor;
        } while (cursor != null);
        Assert.Equal(23, ids.Count);
    }

    [Fact]
    public async Task ServerDatabaseAndHalfOpenTimeFiltersUseExecutionContext()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(1, context: new SqlConnectionLabel("LibraryServer", "Library")), Token);
        await store.Process(repository, store.Capture(2, seconds: 1, context: new SqlConnectionLabel("LibraryServer", "Archive")), Token);
        await store.Process(repository, store.Capture(3, seconds: 2, context: new SqlConnectionLabel("LibraryServer", "Library")), Token);
        await store.Process(repository, store.Capture(4, seconds: 3, context: new SqlConnectionLabel("OtherServer", "Library")), Token);
        var page = await repository.ReadHistoryAsync(new SqlHistoryRequest(20, SqlHistoryFilter.Executions,
            servers: new[] { "LibraryServer" }, databases: new[] { "Library" }, since: SqliteTestStore.Start, until: SqliteTestStore.Start.AddSeconds(2)), Token);
        Assert.Single(page.Items);
        Assert.Equal(SqliteTestStore.Start, page.Items[0].CreatedAt);
        Assert.Equal(3, (await repository.ReadHistoryAsync(new SqlHistoryRequest(20, SqlHistoryFilter.Executions, databases: new[] { "Library" }), Token)).Items.Count);
    }

    [Fact]
    public async Task SearchIsLiteralAndIncludesTextBeyondPreview()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var sql = new string(' ', 300) + "SELECT N'Lib_Reader%📚';\0END";
        await store.Process(repository, store.Capture(sql: sql), Token);
        foreach (var search in new[] { "Lib_Reader%", "📚", "\0END", " ", "" })
        {
            var page = await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Executions, search: search), Token);
            Assert.Single(page.Items);
            Assert.InRange(page.Items[0].Preview.Length, 0, 240);
        }
        // History 的搜尋目標只有 SQL 全文；文件顯示名稱與 Favorite 的名稱欄位不在其中。
        foreach (var search in new[] { "' OR 1=1--", "DoesNotExist", "Library.sql" })
            Assert.Empty((await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Executions, search: search), Token)).Items);
    }

    /// <remarks>
    /// 與 SQL Search 同一套規則：預設不分大小寫，兩顆修飾各自再縮小。整個字照識別字的形狀認，
    /// 所以 <c>Lib</c> 不算 <c>Lib_Reader</c> 裡的一個字，而 <c>N'</c> 後面那一段仍是。
    /// </remarks>
    [Theory]
    [InlineData("lib_reader", TextMatchOptions.None, true)]
    [InlineData("lib_reader", TextMatchOptions.MatchCasing, false)]
    [InlineData("Lib_Reader", TextMatchOptions.MatchCasing, true)]
    [InlineData("Lib", TextMatchOptions.WholeWord, false)]
    [InlineData("lib_reader", TextMatchOptions.WholeWord, true)]
    [InlineData("lib_reader", TextMatchOptions.MatchCasing | TextMatchOptions.WholeWord, false)]
    public async Task SearchHonorsMatchOptions(string search, TextMatchOptions options, bool found)
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(sql: "SELECT N'Lib_Reader' FROM Loan;"), Token);

        var page = await repository.ReadHistoryAsync(
            new SqlHistoryRequest(10, SqlHistoryFilter.Executions, search: search, matchOptions: options), Token);

        Assert.Equal(found ? 1 : 0, page.Items.Count);
    }

    /// <summary>換了比對方式的游標不能接著用：同一個位置在另一種比法下是另一份答案。</summary>
    [Fact]
    public async Task CursorIsBoundToMatchOptions()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        for (var i = 1; i <= 3; i++)
            await store.Process(repository, store.Capture(i, $"SELECT {i} FROM Lib_Reader;", seconds: i), Token);
        var first = await repository.ReadHistoryAsync(new SqlHistoryRequest(1, SqlHistoryFilter.Executions, search: "lib_reader"), Token);
        Assert.NotNull(first.NextCursor);

        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.ReadHistoryAsync(new SqlHistoryRequest(1, SqlHistoryFilter.Executions,
            search: "lib_reader", cursor: first.NextCursor, matchOptions: TextMatchOptions.MatchCasing), Token));
    }

    [Theory]
    [InlineData(7, long.MaxValue)]
    [InlineData(1000, 100)]
    public async Task SearchScanBudgetBoundsEachPageAndResumesWithoutGapsOrDuplicates(int candidates, long bytes)
    {
        using var store = new SqliteTestStore();
        var repository = await SqliteTestRepository.OpenAsync(store.Path, Token, searchBudget: new SqliteSearchBudget(candidates, bytes));
        var expected = new List<Guid>();
        for (var i = 1; i <= 40; i++)
        {
            // 每列 SQL 超過 50 個 code units（100 位元組），位元組預算因此每頁只容得下一列候選。
            var table = i % 9 == 0 || i == 1 ? "Lib_Reader" : "Lib_Tag";
            await store.Process(repository, store.Capture(i, $"SELECT {i:D3}, N'padding padding padding' FROM {table};", seconds: i), Token);
        }
        var all = (await repository.ReadHistoryAsync(new SqlHistoryRequest(200, SqlHistoryFilter.Executions), Token)).Items;
        Assert.Equal(40, all.Count);
        var perPage = bytes == long.MaxValue ? candidates : 1;
        var ids = new HashSet<Guid>();
        var actual = new List<Guid>();
        string? cursor = null;
        var boundary = DateTimeOffset.MaxValue;
        var partial = 0;
        do
        {
            var page = await repository.ReadHistoryAsync(new SqlHistoryRequest(2, SqlHistoryFilter.Executions, "Lib_Reader", cursor: cursor), Token);
            Assert.InRange(page.Items.Count, 0, 2);
            foreach (var item in page.Items) { Assert.True(ids.Add(item.ItemId)); actual.Add(item.ItemId); }
            if (page.IsSearchPartial)
            {
                partial++;
                Assert.NotNull(page.NextCursor);
                var through = page.SearchedThrough ?? throw new InvalidOperationException("History 部分搜尋必須回報時間。");
                // 這一頁實際檢查過的候選就是 [through, 上一頁邊界) 之間的列，數量不得超過預算。
                Assert.InRange(all.Count(item => item.CreatedAt >= through && item.CreatedAt < boundary), 1, perPage);
                Assert.All(page.Items, item => Assert.True(item.CreatedAt >= through));
                boundary = through;
            }
            else Assert.Null(page.SearchedThrough);
            cursor = page.NextCursor;
        } while (cursor != null);
        Assert.Equal(all.Where(item => item.Preview.Contains("Lib_Reader")).Select(item => item.ItemId), actual);
        Assert.True(partial >= 40 / perPage - 1, partial.ToString());
    }

    [Fact]
    public async Task SearchWithoutAnyMatchStopsAtBudgetAndReportsProgress()
    {
        using var store = new SqliteTestStore();
        var repository = await SqliteTestRepository.OpenAsync(store.Path, Token, searchBudget: new SqliteSearchBudget(5, long.MaxValue));
        for (var i = 1; i <= 12; i++) await store.Process(repository, store.Capture(i, $"SELECT {i} FROM Lib_Tag;", seconds: i), Token);
        var first = await repository.ReadHistoryAsync(new SqlHistoryRequest(50, SqlHistoryFilter.Executions, "DoesNotExist"), Token);
        Assert.Empty(first.Items);
        Assert.True(first.IsSearchPartial);
        Assert.Equal(SqliteTestStore.Start.AddSeconds(8), first.SearchedThrough);
        var second = await repository.ReadHistoryAsync(new SqlHistoryRequest(50, SqlHistoryFilter.Executions, "DoesNotExist", cursor: first.NextCursor), Token);
        Assert.Equal(SqliteTestStore.Start.AddSeconds(3), second.SearchedThrough);
        // 剩下兩列不到預算：掃完就是真正的結尾，不留一個只會回空頁的「繼續搜尋」。
        var last = await repository.ReadHistoryAsync(new SqlHistoryRequest(50, SqlHistoryFilter.Executions, "DoesNotExist", cursor: second.NextCursor), Token);
        Assert.Empty(last.Items);
        Assert.False(last.IsSearchPartial);
        Assert.Null(last.NextCursor);
        // 游標指紋照舊綁定搜尋字串。
        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.ReadHistoryAsync(
            new SqlHistoryRequest(50, SqlHistoryFilter.Executions, "DoesNot", cursor: first.NextCursor), Token));
    }

    [Fact]
    public async Task CursorCannotBeReusedWithDifferentFiltersOrDatabase()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        for (var i = 1; i <= 3; i++) await store.Process(repository, store.Capture(i, Distinct(i)), Token);
        var page = await repository.ReadHistoryAsync(new SqlHistoryRequest(1, SqlHistoryFilter.Executions), Token);
        Assert.NotNull(page.NextCursor);
        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.ReadHistoryAsync(
            new SqlHistoryRequest(1, SqlHistoryFilter.Executions, servers: new[] { "LibraryServer" }, cursor: page.NextCursor), Token));
        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => repository.ReadHistoryAsync(new SqlHistoryRequest(1, cursor: "!invalid!"), Token));
        using var other = new SqliteTestStore();
        var otherRepository = await other.Open(Token);
        await Assert.ThrowsAsync<SqlMemoryStorageException>(() => otherRepository.ReadHistoryAsync(
            new SqlHistoryRequest(1, SqlHistoryFilter.Executions, cursor: page.NextCursor), Token));
        Assert.Equal(2, (await repository.ReadHistoryAsync(new SqlHistoryRequest(20, SqlHistoryFilter.Executions, cursor: page.NextCursor), Token)).Items.Count);
    }

    [Fact]
    public async Task DeletingAnExecutionRemovesItsEventSelectionRevisionAndUnreferencedContent()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var library = new SqlConnectionLabel("LibraryServer", "Library");
        await store.Process(repository, store.Capture(1, selected: "SELECT CopyNo FROM Cat_BookCopy;", context: library), Token);
        await store.Process(repository, store.Capture(2, selected: "SELECT * FROM Loan;", seconds: 1), Token);
        var executions = (await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Executions), Token)).Items;
        Assert.Equal(2, executions.Count);
        var older = executions[1];

        Assert.Equal(SqlHistoryDeleteResult.Deleted, await repository.DeleteHistoryAsync(older, Token));

        Assert.Equal(new[] { executions[0].ItemId },
            (await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Executions), Token)).Items.Select(item => item.ItemId));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Executions;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Revisions WHERE IsExecutionSelection=1;"));
        Assert.Null(await repository.ReadContentAsync(older.ContentId, Token));
        // 連線只存在 History 投影：刪掉的那一筆帶走自己的連線，不留下另一張表裡的孤立列等維護回收。
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM History WHERE Server='LibraryServer';"));
        Assert.Equal(SqlHistoryDeleteResult.NotFound, await repository.DeleteHistoryAsync(older, Token));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task DeletingRecoveryKeepsTheSessionAndTheNextCaptureWritesItAgain()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(1, kind: SqlCaptureKind.DraftIdle), Token);
        var recovery = (await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Drafts), Token)).Items
            .Single(item => item.RevisionId == null);

        Assert.Equal(SqlHistoryDeleteResult.NotFound,
            await repository.DeleteHistoryAsync(recovery with { SessionId = Guid.NewGuid() }, Token));
        Assert.Equal(SqlHistoryDeleteResult.Deleted, await repository.DeleteHistoryAsync(recovery, Token));

        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Sessions;"));
        // 自動版本仍引用同一份內容，刪除 Recovery 不得把它回收。
        Assert.NotNull(await repository.ReadContentAsync(recovery.ContentId, Token));
        await store.Process(repository, store.Capture(2, kind: SqlCaptureKind.DraftIdle, seconds: 1), Token);
        Assert.Equal(1L, store.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task DeletingADraftLeavesProtectedRevisionsAndFavoriteContentToMaintenance()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(1, "SELECT * FROM Lib_Reader;", SqlCaptureKind.DraftIdle), Token);
        var first = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        var favorite = new SqlFavorite(Guid.NewGuid(), "讀者查詢", null, first!.LatestRevision!.RevisionId, null, null);
        await repository.SaveFavoriteAsync(new SqlFavoriteSave(favorite, null, SqliteTestStore.Start), Token);
        await store.Process(repository, store.Capture(2, "SELECT * FROM Lib_Tag;", SqlCaptureKind.DraftIdle, seconds: 900), Token);
        var drafts = (await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Drafts), Token)).Items
            .Where(item => item.RevisionId != null).ToArray();
        Assert.Equal(2, drafts.Length);

        foreach (var draft in drafts)
            Assert.Equal(SqlHistoryDeleteResult.Deleted, await repository.DeleteHistoryAsync(draft, Token));

        Assert.DoesNotContain((await repository.ReadHistoryAsync(new SqlHistoryRequest(10, SqlHistoryFilter.Drafts), Token)).Items,
            item => item.RevisionId != null);
        // 舊版本同時是收藏與子版本的根，新版本是 Session head；兩者都不刪，只離開清單。
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions;"));
        Assert.NotNull(await repository.ReadFavoriteAsync(favorite.FavoriteId, Token));
        foreach (var draft in drafts) Assert.NotNull(await repository.ReadContentAsync(draft.ContentId, Token));
        Assert.Null(store.Scalar("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task CanceledQueriesAndCommitsDoNotWriteAnything()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReadHistoryAsync(new SqlHistoryRequest(20), cancellation.Token));
        var write = new SqlCapturePlanner().Prepare(store.Capture(), null, SqliteTestStore.Policy);
        Assert.NotNull(write);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.CommitAsync(write, null, cancellation.Token));
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Contents;"));
    }

    [Fact]
    public async Task BusyWriterFailsWithinConfiguredTimeoutWithoutPartialData()
    {
        using var store = new SqliteTestStore();
        var repository = await SqliteTestRepository.OpenAsync(store.Path, Token, busyTimeoutSeconds: 1);
        using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        blocker.Open();
        using var transaction = blocker.BeginTransaction(deferred: false);
        var error = await Assert.ThrowsAsync<SqliteException>(() => store.Process(repository, store.Capture(), Token));
        Assert.Equal(5, error.SqliteErrorCode);
        Assert.Equal(0L, store.Scalar("SELECT count(*) FROM Contents;"));
    }

    [Fact]
    public async Task FilteredKeysetCanUseCompositeIndexWithoutSortingEntireHistory()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"EXPLAIN QUERY PLAN SELECT EntryKey FROM History WHERE Server='LibraryServer' AND DatabaseName='Library'
AND (CreatedAt,EntryKey) < (100,'e00000000000000000000000000000000') ORDER BY CreatedAt DESC,EntryKey DESC LIMIT 20;";
        using (var reader = command.ExecuteReader())
        {
            var plans = new List<string>();
            while (reader.Read()) plans.Add(reader.GetString(3));
            Assert.Contains(plans, plan => plan.Contains("IX_History_ServerDatabaseTime"));
            Assert.DoesNotContain(plans, plan => plan.Contains("TEMP B-TREE"));
        }
        // 實際分頁查詢（含搜尋時帶出的 SqlBytes）在各種篩選組合下都要沿時間索引串流，搜尋預算才限制得了讀入的 BLOB。
        // 單選與多選兩種形狀都要走這一輪：多選那一支若讓 SQLite 拿伺服器索引去查，ORDER BY 就落在索引後段，
        // 它會改為建一棵暫存 b-tree 排序整份結果，而預算只限制得了讀進來的位元組，限制不了那一次排序。
        foreach (var (server, database) in new[]
        {
            ("h.Server='LibraryServer'", "h.DatabaseName='Library'"),
            ("+h.Server IN ('LibraryServer','ArchiveServer')", "+h.DatabaseName IN ('Library','Archive')")
        })
        {
            var filters = new[] { "h.Kind=1", server, database, "h.CreatedAt >= 1", "h.CreatedAt < 100",
                "(h.CreatedAt, h.EntryKey) < (100, 'e00000000000000000000000000000000')" };
            for (var mask = 0; mask < 1 << filters.Length; mask++)
            {
                var conditions = filters.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
                command.CommandText = "EXPLAIN QUERY PLAN " + SqliteCaptureStore.HistoryPageSql(conditions, includeSql: true);
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$limit", 2001);
                using var reader = command.ExecuteReader();
                var plans = new List<string>();
                while (reader.Read()) plans.Add(reader.GetString(3));
                Assert.DoesNotContain(plans, plan => plan.Contains("TEMP B-TREE"));
            }
        }
    }

    /// <summary>多選的條件由 <c>SqliteConnectionFilter</c> 組出來；SQL 的形狀與上面那一輪守住的必須是同一個。</summary>
    [Fact]
    public void MultipleConnectionNamesKeepTheTimeIndexStreaming()
    {
        var conditions = new List<string>();
        var parameters = new List<(string Name, object? Value)>();
        SqliteConnectionFilter.Append(conditions, parameters, "h",
            new[] { "LibraryServer", "ArchiveServer" }, new[] { "Library" });

        Assert.Equal(new[] { "+h.Server IN ($server0,$server1)", "h.DatabaseName=$database" }, conditions);
        Assert.Equal(new[] { "$server0", "$server1", "$database" }, parameters.Select(parameter => parameter.Name).ToArray());
        Assert.Equal(new object?[] { "LibraryServer", "ArchiveServer", "Library" },
            parameters.Select(parameter => parameter.Value).ToArray());
    }
}
