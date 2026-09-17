using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

public sealed class SqliteFavoriteRevisionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SqlFavorite NewFavorite() =>
        new(Guid.NewGuid(), "館藏複本", null, Guid.NewGuid(), null, null);

    /// <summary>建立收藏並依序改 SQL；回傳新到舊的版本識別碼。</summary>
    private static async Task<List<Guid>> CreateWithEdits(SqliteTestRepository repository, SqlFavorite favorite, params string[] edits)
    {
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.SaveFavoriteAsync(
            new SqlFavoriteSave(favorite, null, SqliteTestStore.Start, "SELECT CopyNo FROM Cat_BookCopy;"), Token));
        var revisions = new List<Guid> { favorite.CurrentRevisionId };
        for (var i = 0; i < edits.Length; i++)
            revisions.Insert(0, await EditSql(repository, favorite.FavoriteId, edits[i], SqliteTestStore.Start.AddMinutes(i + 1)));
        return revisions;
    }

    /// <summary>以最新讀到的版本改 SQL；回傳新版本識別碼。</summary>
    private static async Task<Guid> EditSql(SqliteTestRepository repository, Guid favoriteId, string sql, DateTimeOffset at)
    {
        var item = await repository.ReadFavoriteAsync(favoriteId, Token);
        Assert.NotNull(item);
        var revision = Guid.NewGuid();
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.SaveFavoriteAsync(
            new SqlFavoriteSave(item.Favorite with { CurrentRevisionId = revision }, item.Version, at, sql), Token));
        return revision;
    }

    private static async Task<List<SqlFavoriteRevisionItem>> ReadAll(SqliteTestRepository repository, Guid favoriteId, int pageSize)
    {
        var items = new List<SqlFavoriteRevisionItem>();
        string? cursor = null;
        do
        {
            var page = await repository.ReadFavoriteRevisionsAsync(new SqlFavoriteRevisionRequest(favoriteId, pageSize, cursor), Token);
            Assert.InRange(page.Items.Count, page.NextCursor == null ? 0 : 1, pageSize);
            Assert.False(page.IsSearchPartial);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor != null);
        return items;
    }

    [Fact]
    public async Task ListsOwnRevisionsNewestFirstAcrossPagesWithSingleCurrent()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var favorite = NewFavorite();
        var expected = await CreateWithEdits(repository, favorite,
            "SELECT CopyNo, Branch FROM Cat_BookCopy;", "SELECT * FROM Loan;", "SELECT * FROM LoanDetail;");

        foreach (var pageSize in new[] { 1, 2, 3, 50 })
        {
            var items = await ReadAll(repository, favorite.FavoriteId, pageSize);
            Assert.Equal(expected, items.Select(item => item.RevisionId));
            Assert.Equal(new[] { true, false, false, false }, items.Select(item => item.IsCurrent));
        }

        var newest = (await ReadAll(repository, favorite.FavoriteId, 50))[0];
        Assert.Equal(SqlContent.Create("SELECT * FROM LoanDetail;").ContentId, newest.ContentId);
        Assert.Equal("SELECT * FROM LoanDetail;", newest.Preview);
        Assert.Equal("SELECT * FROM LoanDetail;".Length, newest.Length);
        Assert.Equal(SqliteTestStore.Start.AddMinutes(3), newest.CreatedAt);
        Assert.Equal(SqlRevisionReason.Favorite, newest.Reason);
    }

    [Fact]
    public async Task SameTimestampUsesRevisionIdAsStableTieBreaker()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var favorite = NewFavorite();
        await CreateWithEdits(repository, favorite);
        for (var i = 0; i < 4; i++)
            await EditSql(repository, favorite.FavoriteId, "SELECT " + i + " FROM Copy;", SqliteTestStore.Start);
        var whole = await ReadAll(repository, favorite.FavoriteId, 50);
        var paged = await ReadAll(repository, favorite.FavoriteId, 1);
        Assert.Equal(5, whole.Count);
        Assert.Equal(whole.Select(item => item.RevisionId), paged.Select(item => item.RevisionId));
        Assert.Equal(whole.Select(item => item.RevisionId.ToString("N")).OrderByDescending(id => id, StringComparer.Ordinal),
            whole.Select(item => item.RevisionId.ToString("N")));
        Assert.Single(whole, item => item.IsCurrent);
    }

    [Fact]
    public async Task CurrentRevisionReferencedFromHistoryIsMergedInTimeOrder()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        await store.Process(repository, store.Capture(seconds: 90), Token);
        var head = await repository.ReadSessionAsync(store.Session.SessionId, Token);
        Assert.NotNull(head?.LatestRevision);
        var favorite = NewFavorite() with { CurrentRevisionId = head.LatestRevision.RevisionId };
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.SaveFavoriteAsync(
            new SqlFavoriteSave(favorite, null, SqliteTestStore.Start), Token));

        var referenced = Assert.Single(await ReadAll(repository, favorite.FavoriteId, 50));
        Assert.True(referenced.IsCurrent);
        Assert.Equal(SqlRevisionReason.BeforeExecute, referenced.Reason);

        // 收藏自己的版本比目前引用的版本早也晚：目前版本要夾在中間，而且每種頁大小都不重複、不遺漏。
        var early = await EditSql(repository, favorite.FavoriteId, "SELECT 1 FROM Branch;", SqliteTestStore.Start);
        var late = await EditSql(repository, favorite.FavoriteId, "SELECT 2 FROM Branch;", SqliteTestStore.Start.AddMinutes(5));
        var item = await repository.ReadFavoriteAsync(favorite.FavoriteId, Token);
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.SaveFavoriteAsync(new SqlFavoriteSave(
            item!.Favorite with { CurrentRevisionId = referenced.RevisionId }, item.Version, SqliteTestStore.Start.AddMinutes(6)), Token));

        foreach (var pageSize in new[] { 1, 2, 3 })
        {
            var items = await ReadAll(repository, favorite.FavoriteId, pageSize);
            Assert.Equal(new[] { late, referenced.RevisionId, early }, items.Select(i => i.RevisionId));
            Assert.Equal(new[] { false, true, false }, items.Select(i => i.IsCurrent));
        }
    }

    [Fact]
    public async Task MissingFavoriteReturnsEmptyPageAndCursorIsBoundToFavorite()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var favorite = NewFavorite();
        await CreateWithEdits(repository, favorite, "SELECT * FROM Loan;");
        var first = await repository.ReadFavoriteRevisionsAsync(new SqlFavoriteRevisionRequest(favorite.FavoriteId, 1), Token);
        Assert.NotNull(first.NextCursor);

        var other = NewFavorite();
        await CreateWithEdits(repository, other);
        foreach (var cursor in new[] { first.NextCursor, "!invalid!", Convert.ToBase64String(Encoding.UTF8.GetBytes("revision2|x|y|1|z")) })
        {
            var error = await Assert.ThrowsAsync<SqlMemoryStorageException>(() =>
                repository.ReadFavoriteRevisionsAsync(new SqlFavoriteRevisionRequest(other.FavoriteId, 1, cursor), Token));
            Assert.Equal(SqlMemoryStorageErrorKind.InvalidCursor, error.Kind);
        }

        var missing = await repository.ReadFavoriteRevisionsAsync(new SqlFavoriteRevisionRequest(Guid.NewGuid(), 10), Token);
        Assert.Empty(missing.Items);
        Assert.Null(missing.NextCursor);

        // 移除收藏不刪版本，但時間軸不再列出一個已不存在的收藏。
        var item = await repository.ReadFavoriteAsync(favorite.FavoriteId, Token);
        await repository.DeleteFavoriteAsync(favorite.FavoriteId, item!.Version, Token);
        Assert.Empty((await repository.ReadFavoriteRevisionsAsync(new SqlFavoriteRevisionRequest(favorite.FavoriteId, 10), Token)).Items);
        Assert.Equal(2L, store.Scalar("SELECT count(*) FROM Revisions WHERE FavoriteId='" + favorite.FavoriteId.ToString("N") + "';"));
    }

    [Fact]
    public async Task RevertingThroughEditAppendsANewVersionAndQuotaTrimsTheTimeline()
    {
        using var store = new SqliteTestStore();
        var repository = await store.Open(Token);
        var favorite = NewFavorite();
        await CreateWithEdits(repository, favorite, "SELECT * FROM Loan;", "SELECT * FROM LoanDetail;");
        var oldest = (await ReadAll(repository, favorite.FavoriteId, 50)).Last();
        var content = await repository.ReadContentAsync(oldest.ContentId, Token);
        var item = await repository.ReadFavoriteAsync(favorite.FavoriteId, Token);
        var revert = new SqlFavoriteSave(item!.Favorite with { CurrentRevisionId = Guid.NewGuid() }, item.Version,
            SqliteTestStore.Start.AddHours(1), content!.SqlText);
        Assert.Equal(SqlFavoriteWriteResult.Committed, await repository.SaveFavoriteAsync(revert, Token));

        var items = await ReadAll(repository, favorite.FavoriteId, 50);
        Assert.Equal(4, items.Count);
        Assert.Equal(revert.Favorite.CurrentRevisionId, items[0].RevisionId);
        Assert.True(items[0].IsCurrent);
        // 回溯是新增，不是改寫：舊版本的識別碼與內容位址都還在，內容照常去重。
        Assert.Equal(oldest, items[3]);
        Assert.Equal(oldest.ContentId, items[0].ContentId);
        // 過期 token 的回溯與一般編輯同樣被拒絕。
        Assert.Equal(SqlFavoriteWriteResult.Conflict, await repository.SaveFavoriteAsync(revert, Token));

        await SqliteTestStore.Drain(repository, new SqlRetentionPolicy(null, null, null, maxRevisionsPerFavorite: 2));
        var trimmed = await ReadAll(repository, favorite.FavoriteId, 50);
        Assert.Equal(new[] { revert.Favorite.CurrentRevisionId, items[1].RevisionId }, trimmed.Select(i => i.RevisionId));
    }

    [Fact]
    public async Task RevisionPagesStreamThroughTheFavoriteIndexWithoutTemporarySort()
    {
        using var store = new SqliteTestStore();
        await store.Open(Token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        foreach (var after in new[] { false, true })
        {
            command.CommandText = "EXPLAIN QUERY PLAN " + SqliteFavoriteStore.FavoriteRevisionPageSql(after);
            command.Parameters.Clear();
            foreach (var (name, value) in new (string, object)[] { ("$id", "f"), ("$time", 1L), ("$revision", "r"), ("$limit", 20) })
                command.Parameters.AddWithValue(name, value);
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            Assert.Contains(plan, line => line.Contains("IX_Revisions_Favorite"));
            Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE"));
            Assert.DoesNotContain(plan, line => line.StartsWith("SCAN r", StringComparison.Ordinal));
        }
    }
}
