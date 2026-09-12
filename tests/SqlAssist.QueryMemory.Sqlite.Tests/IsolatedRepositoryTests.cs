using System;
using System.IO;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;
using SqlAssist.QueryMemory.Hosting;
using Xunit;

namespace SqlAssist.QueryMemory.Sqlite.Tests;

public sealed class IsolatedRepositoryTests
{
    [Fact]
    public async Task FailedInitializationCanBeFollowedByAnotherRepository()
    {
        using var store = new SqliteTestStore();
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.Path, "保留損壞資料庫，不重建");
        var token = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() => IsolatedQueryMemoryRepository.OpenAsync(store.Path, null, token));
        Assert.Equal("保留損壞資料庫，不重建", File.ReadAllText(store.Path));
        using var reopened = await IsolatedQueryMemoryRepository.OpenAsync(Path.Combine(store.DirectoryPath, "new.db"), null, token);
        Assert.Contains("3.53.4", await reopened.ProbeAsync(token));
    }

    [Fact]
    public async Task CoreDtosRoundTripThroughIsolatedDomainAndDomainCanReopen()
    {
        using var store = new SqliteTestStore();
        var token = TestContext.Current.CancellationToken;
        var repository = await IsolatedQueryMemoryRepository.OpenAsync(store.Path, null, token);
        try
        {
            var processor = new QueryMemoryProcessor(repository, new QueryRevisionEngine());
            await processor.ProcessAsync(store.Capture(selected: "SELECT 1"), SqliteTestStore.Policy, token);
            var state = await repository.ReadSessionAsync(store.Session.SessionId, token);
            Assert.NotNull(state);
            var page = await repository.ReadHistoryAsync(new QueryHistoryRequest(20, QueryHistoryKind.Executed), token);
            var entry = Assert.Single(page.Items);
            Assert.Equal("SELECT 1", (await repository.ReadContentAsync(entry.ContentId, token))?.SqlText);
            Assert.Contains("3.53.4", await repository.ProbeAsync(token));
        }
        finally { repository.Dispose(); }
        repository.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => repository.ReadSessionAsync(store.Session.SessionId, token));
        using var reopened = await IsolatedQueryMemoryRepository.OpenAsync(store.Path, null, token);
        Assert.NotNull(await reopened.ReadSessionAsync(store.Session.SessionId, token));
    }
}
