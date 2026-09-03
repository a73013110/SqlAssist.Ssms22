using System;
using System.Data;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Querying;
using Xunit;

namespace SqlAssist.Metadata.Tests.Caching;

public sealed class SqlMetadataCatalogRegistryTests
{
    [Fact]
    public void 同一個快取鍵共用同一份目錄()
    {
        var registry = new SqlMetadataCatalogRegistry();
        var first = new FakeConnectionSource("server-a|db1");
        var second = new FakeConnectionSource("server-a|db1");

        Assert.Same(registry.GetOrCreate(first), registry.GetOrCreate(second));
    }

    /// <summary>
    /// 多出來的連線來源沒有人會用到，必須由註冊表釋放。
    /// </summary>
    /// <remarks>
    /// 先前是呼叫端自己釋放，但呼叫端無法判斷自己交出去的那一份是不是正被共用，
    /// 結果關掉一個查詢視窗就把其他視窗還在用的連線一起釋放掉。
    /// </remarks>
    [Fact]
    public void 重複的連線來源由註冊表釋放()
    {
        var registry = new SqlMetadataCatalogRegistry();
        var owned = new FakeConnectionSource("server-a|db1");
        var redundant = new FakeConnectionSource("server-a|db1");

        registry.GetOrCreate(owned);
        registry.GetOrCreate(redundant);

        Assert.True(redundant.IsDisposed);
        Assert.False(owned.IsDisposed);
    }

    [Fact]
    public void 不同快取鍵各自建立目錄()
    {
        var registry = new SqlMetadataCatalogRegistry();
        var first = new FakeConnectionSource("server-a|db1");
        var second = new FakeConnectionSource("server-a|db2");

        Assert.NotSame(registry.GetOrCreate(first), registry.GetOrCreate(second));
        Assert.False(first.IsDisposed);
        Assert.False(second.IsDisposed);
    }

    /// <remarks>
    /// 查詢一律寫成不加限定的 <c>sys.objects</c>，換資料庫換的是連線而不是 SQL，
    /// 所以同一條連線的另一個資料庫只是另一把鍵。
    /// </remarks>
    [Fact]
    public void 跨資料庫在同一台伺服器上另建目錄()
    {
        var registry = new SqlMetadataCatalogRegistry();
        var editor = new FakeConnectionSource("server-a|db1");

        var local = registry.GetOrCreate(editor);
        var archive = registry.GetOrCreateFor(editor, "LibArchive");

        Assert.NotSame(local, archive);
        Assert.Same(archive, registry.GetOrCreateFor(editor, "LibArchive"));
        Assert.Same(archive, registry.GetOrCreateFor(editor, "libarchive"));
    }

    /// <remarks>
    /// 跨資料庫先建過同一個資料庫的目錄時要改掛成編輯器的，不能重建一份——
    /// 重建的症狀是同一個資料庫被查兩輪，而兩份的過期時間各走各的。
    /// </remarks>
    [Fact]
    public void 查詢視窗切到已經建過的資料庫時沿用同一份()
    {
        var registry = new SqlMetadataCatalogRegistry();
        var editor = new FakeConnectionSource("server-a|db1");
        var archive = registry.GetOrCreateFor(editor, "LibArchive");

        var switched = new FakeConnectionSource("server-a|libarchive");

        Assert.Same(archive, registry.GetOrCreate(switched));
    }

    /// <remarks>
    /// 跨資料庫的目錄是使用者打字打出來的，沒有上限就是一條隨著輸入成長的記憶體：
    /// 第一層快照常駐，而一個資料庫動輒幾千列。
    /// </remarks>
    [Fact]
    public void 跨資料庫目錄超過上限時淘汰最久沒用到的()
    {
        var registry = new SqlMetadataCatalogRegistry();
        var editor = new FakeConnectionSource("server-a|db1");

        var first = registry.GetOrCreateFor(editor, "Archive0");

        for (var index = 1; index <= 8; index++)
        {
            registry.GetOrCreateFor(editor, "Archive" + index);
        }

        Assert.NotSame(first, registry.GetOrCreateFor(editor, "Archive0"));
    }

    /// <remarks>
    /// 最近用過的不該被淘汰：那是使用者正在打的那個資料庫，淘汰它等於下一次
    /// 按鍵重查一輪。
    /// </remarks>
    [Fact]
    public void 淘汰時保留最近用過的()
    {
        var registry = new SqlMetadataCatalogRegistry();
        var editor = new FakeConnectionSource("server-a|db1");

        var kept = registry.GetOrCreateFor(editor, "Archive0");

        for (var index = 1; index <= 7; index++)
        {
            registry.GetOrCreateFor(editor, "Archive" + index);
        }

        // 再碰一次，讓 Archive0 不再是最久沒用到的那一個。
        Assert.Same(kept, registry.GetOrCreateFor(editor, "Archive0"));
        registry.GetOrCreateFor(editor, "Archive8");

        Assert.Same(kept, registry.GetOrCreateFor(editor, "Archive0"));
    }

    /// <remarks>
    /// 編輯器連線的目錄是使用者正在看的那個資料庫，不參與淘汰。
    /// </remarks>
    [Fact]
    public void 查詢視窗自己的目錄不會被跨資料庫擠掉()
    {
        var registry = new SqlMetadataCatalogRegistry();
        var editor = new FakeConnectionSource("server-a|db1");
        var pinned = registry.GetOrCreate(editor);

        for (var index = 0; index <= 12; index++)
        {
            registry.GetOrCreateFor(editor, "Archive" + index);
        }

        Assert.Same(pinned, registry.GetOrCreate(new FakeConnectionSource("server-a|db1")));
    }

    private sealed class FakeConnectionSource : ISqlConnectionSource, IDisposable
    {
        public FakeConnectionSource(string cacheKey, string serverCacheKey = "server-a")
        {
            CacheKey = cacheKey;
            ServerCacheKey = serverCacheKey;
        }

        public string CacheKey { get; }

        public string ServerCacheKey { get; }

        public string DatabaseName => "db";

        public bool IsDisposed { get; private set; }

        public IDbConnection OpenConnection() => throw new NotSupportedException();

        public void Dispose() => IsDisposed = true;
    }
}
