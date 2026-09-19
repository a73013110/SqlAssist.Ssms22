using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Metadata.Querying;
using SqlAssist.Metadata.Search;
using Xunit;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// 索引快取：位元組預算、平行建置，以及「標記過期」與「整批丟掉」的差別。
/// </summary>
public sealed class SqlCatalogSearchIndexCacheTests
{
    private static readonly DateTime Earlier = new(2025, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2025, 6, 2, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Latest = new(2025, 9, 4, 11, 15, 0, DateTimeKind.Utc);

    /// <summary>
    /// 預算滿了淘汰最久沒用到的那一份。
    /// </summary>
    /// <remarks>
    /// 照份數算上限的症狀是四個大庫就把 SSMS 的行程撐爆，而四個小庫又白白丟掉
    /// 明明留得住的東西——一份索引的大小差到三個數量級。
    /// </remarks>
    [Fact]
    public void 位元組預算滿了淘汰最久沒用到的()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Lib_Tag", "V", new string('A', 1000));
        server.Add("LibArchive").WithObject(1, "dbo", "Lib_Tag", "V", new string('B', 1000));

        var cache = new SqlCatalogSearchIndexCache(maxBytes: 3000);

        Assert.NotNull(cache.GetOrBuild(server.SourceFor("Library"), includeDefinitions: true, CancellationToken.None));
        Assert.NotNull(cache.GetOrBuild(server.SourceFor("LibArchive"), includeDefinitions: true, CancellationToken.None));

        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(server.SourceFor("Library").CacheKey, out _));
        Assert.True(cache.TryGet(server.SourceFor("LibArchive").CacheKey, out _));
        Assert.True(cache.Bytes <= cache.MaxBytes, $"佔用 {cache.Bytes} 超過預算 {cache.MaxBytes}");
    }

    /// <summary>
    /// 一份比預算還大的索引仍然留著。
    /// </summary>
    /// <remarks>
    /// 丟掉它的話，使用者對那個資料庫的每一輪搜尋都在重掃全表，而畫面上只看得出「搜尋很慢」。
    /// </remarks>
    [Fact]
    public void 比預算還大的那一份也留著()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Lib_Tag", "V", new string('A', 1000));

        var cache = new SqlCatalogSearchIndexCache(maxBytes: 64);

        Assert.NotNull(cache.GetOrBuild(server.SourceFor("Library"), includeDefinitions: true, CancellationToken.None));
        Assert.Equal(1, cache.Count);
    }

    /// <summary>
    /// 同一個資料庫同時被要好幾次，只建一次。
    /// </summary>
    /// <remarks>
    /// 兩條執行緒各掃一次全表，是這一層最貴的一件事，而兩份結果一模一樣。
    /// </remarks>
    [Fact]
    public async Task 同一個資料庫同時要好幾次只建一次()
    {
        var server = SqlCatalogSearchIndexTests.NewServer();
        var cache = new SqlCatalogSearchIndexCache();
        var source = server.SourceFor("Library");

        var built = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => cache.GetOrBuild(source, includeDefinitions: true, CancellationToken.None))));

        Assert.Equal(1, server.Opened);
        Assert.Equal(1, cache.Builds);
        Assert.All(built, index => Assert.Same(built[0], index));
    }

    /// <summary>
    /// 不同資料庫的索引<b>同時</b>建立。
    /// </summary>
    /// <remarks>
    /// 兩條建置在同一個會合點上互等：整份快取一把鎖的話，第二條要等第一條掃完全表才開始，
    /// 而第一條正在等它——會合逾時，這條測試就失敗。使用者那邊的症狀是勾了五個資料庫之後，
    /// 第五個要等前四個都掃完才開始。
    /// </remarks>
    [Fact]
    public async Task 不同資料庫的索引同時建立()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");
        server.Add("LibArchive").WithObject(1, "dbo", "LoanDetail", "U");

        using var rendezvous = new Barrier(2);
        var cache = new SqlCatalogSearchIndexCache();

        var built = await Task.WhenAll(new[] { "Library", "LibArchive" }
            .Select(name => new RendezvousConnectionSource(server.SourceFor(name), rendezvous))
            .Select(source => Task.Run(
                () => cache.GetOrBuild(source, includeDefinitions: true, CancellationToken.None))));

        Assert.All(built, index => Assert.NotNull(index));
        Assert.Equal(2, cache.Count);
    }

    /// <summary>
    /// 「標記舊了」不是「整批丟掉」：沒有變更過的定義本文留著。
    /// </summary>
    /// <remarks>
    /// 使用者按重新整理通常只是改了一個預存程序。整批丟掉的話，那一下要付整個資料庫的
    /// 定義本文；而那正是第一次搜尋最貴的一段。
    /// </remarks>
    [Fact]
    public void 標記過期之後只重撈變更過的物件()
    {
        var server = new FakeCatalogServer();
        var database = server.Add("Library")
            .WithObject(1, "dbo", "Lib_Tag", "V", "SELECT CopyNo FROM dbo.Loan;", Earlier)
            .WithObject(2, "dbo", "Lib_Reader", "V", "SELECT PUBL_CODE FROM dbo.Copy;", Later)
            .WithSchema("dbo");

        var cache = new SqlCatalogSearchIndexCache();
        var source = server.SourceFor("Library");

        Assert.NotNull(cache.GetOrBuild(source, includeDefinitions: true, CancellationToken.None));

        database.Rewrite(1, "這一份不應該出現在重新整理之後的索引裡。");
        database.Touch(2, "SELECT Branch FROM dbo.Copy;", Latest);
        cache.Invalidate();

        Assert.False(cache.IsFresh(source.CacheKey));

        var refreshed = cache.GetOrBuild(source, includeDefinitions: true, CancellationToken.None);

        Assert.Equal("SELECT CopyNo FROM dbo.Loan;", refreshed!.Definitions!.For(1));
        Assert.Equal("SELECT Branch FROM dbo.Copy;", refreshed.Definitions.For(2));

        // 重新整理過就不再是過期的；下一輪直接用，不會每一次都重掃。
        Assert.True(cache.IsFresh(source.CacheKey));
        Assert.Equal(2, cache.Builds);
    }

    /// <summary>整批丟掉之後是從頭建，連版本戳都沒有了。</summary>
    [Fact]
    public void 整批丟掉之後從頭建()
    {
        var server = SqlCatalogSearchIndexTests.NewServer();
        var cache = new SqlCatalogSearchIndexCache();
        var source = server.SourceFor("Library");

        cache.GetOrBuild(source, includeDefinitions: true, CancellationToken.None);
        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
        Assert.False(cache.IsFresh(source.CacheKey));

        cache.GetOrBuild(source, includeDefinitions: true, CancellationToken.None);

        Assert.Equal(2, server.Opened);
    }

    /// <summary>只有第一段時，要第二段不算命中——但也不重掃第一段。</summary>
    [Fact]
    public void 要第二段時只補第二段()
    {
        var server = SqlCatalogSearchIndexTests.NewServer();
        var cache = new SqlCatalogSearchIndexCache();
        var source = server.SourceFor("Library");

        var first = cache.GetOrBuild(source, includeDefinitions: false, CancellationToken.None);
        server.Commands.Clear();

        var second = cache.GetOrBuild(source, includeDefinitions: true, CancellationToken.None);

        Assert.Null(first!.Definitions);
        Assert.NotNull(second!.Definitions);
        Assert.Same(first.Objects, second.Objects);
        Assert.Single(server.Commands);

        // 第三輪只要第一段：手上那一份已經含第二段，直接用。
        var third = cache.GetOrBuild(source, includeDefinitions: false, CancellationToken.None);
        Assert.Same(second, third);
    }

    /// <summary>失敗不進快取；否則連線恢復之後仍然拿到空的。</summary>
    [Fact]
    public void 失敗不進快取()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").FailsOnOpen = true;

        var cache = new SqlCatalogSearchIndexCache();

        Assert.Null(cache.GetOrBuild(
            server.SourceFor("Library"), includeDefinitions: true, CancellationToken.None));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public void 預算要是正數()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlCatalogSearchIndexCache(maxBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqlCatalogSearchIndexCache(maxDefinitionBytes: -1));
    }

    /// <summary>
    /// 開連線時與另一條執行緒會合的連線來源。
    /// </summary>
    /// <remarks>
    /// 會合逾時就把它記下來，而不是讓執行緒永遠停在那裡：掛住的測試只會讓整份測試回合
    /// 超時，而畫面上看不出是哪一條在等什麼。
    /// </remarks>
    private sealed class RendezvousConnectionSource : ISqlConnectionSource
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private readonly ISqlConnectionSource _inner;
        private readonly Barrier _rendezvous;

        internal RendezvousConnectionSource(ISqlConnectionSource inner, Barrier rendezvous)
        {
            _inner = inner;
            _rendezvous = rendezvous;
        }

        public string CacheKey => _inner.CacheKey;

        public string ServerCacheKey => _inner.ServerCacheKey;

        public string DatabaseName => _inner.DatabaseName;

        public IDbConnection OpenConnection()
        {
            Assert.True(
                _rendezvous.SignalAndWait(Patience),
                $"{DatabaseName} 的索引沒有與另一個資料庫同時建立；建置被整份快取的鎖排成了一列。");

            return _inner.OpenConnection();
        }
    }
}
