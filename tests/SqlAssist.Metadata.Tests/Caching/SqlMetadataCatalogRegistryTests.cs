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

    private sealed class FakeConnectionSource : ISqlConnectionSource, IDisposable
    {
        public FakeConnectionSource(string cacheKey)
        {
            CacheKey = cacheKey;
        }

        public string CacheKey { get; }

        public string DatabaseName => "db";

        public bool IsDisposed { get; private set; }

        public IDbConnection OpenConnection() => throw new NotSupportedException();

        public void Dispose() => IsDisposed = true;
    }
}
