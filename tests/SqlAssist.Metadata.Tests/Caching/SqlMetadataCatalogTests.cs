using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Querying;
using Xunit;

namespace SqlAssist.Metadata.Tests.Caching;

/// <summary>
/// 資料庫說不行時的降級行為。
/// </summary>
/// <remarks>
/// 這一組釘住的是「例外不可以冒到 Ssms22 的平台邊界」：那裡的
/// <c>SqlAssistPlatformGuard</c> 會把每一次都記成一份完整堆疊，而連線斷掉時
/// 使用者每開一次建議清單就失敗一次，紀錄檔灌滿之後真正的程式錯誤就找不到了。
/// </remarks>
public sealed class SqlMetadataCatalogTests
{
    private static readonly SqlObjectInfo AnyObject =
        new(1, "dbo", "PUBLISHER", SqlObjectKind.Table);

    [Fact]
    public async Task 連不上資料庫時回傳空快照而不是擲例外()
    {
        var catalog = CreateCatalog();

        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        Assert.True(snapshot.IsEmpty);
    }

    [Fact]
    public async Task 連不上資料庫時取不到明細而不是擲例外()
    {
        var catalog = CreateCatalog();

        Assert.Null(await catalog.GetDetailAsync(AnyObject, CancellationToken.None));
    }

    [Fact]
    public async Task 連不上資料庫時取不到結構而不是擲例外()
    {
        var catalog = CreateCatalog();

        Assert.Null(await catalog.GetStructureAsync(AnyObject, CancellationToken.None));
    }

    /// <summary>
    /// 失敗不進快取，否則連線恢復之後仍然拿到空的。
    /// </summary>
    [Fact]
    public async Task 失敗過的明細不會被記住()
    {
        var source = new FailingConnectionSource();
        var catalog = new SqlMetadataCatalog(source, TimeSpan.FromMinutes(5));

        await catalog.GetDetailAsync(AnyObject, CancellationToken.None);
        await catalog.GetDetailAsync(AnyObject, CancellationToken.None);

        Assert.False(catalog.TryGetCachedDetail(AnyObject.ObjectId, out _));
        Assert.Equal(2, source.Attempts);
    }

    /// <summary>
    /// 剛失敗過的目標不再每一次按鍵都重撞一次。
    /// </summary>
    /// <remarks>
    /// 失敗刻意不進快取，而空快照永遠不算新鮮——兩條加起來，連不上的目標會變成
    /// 每一次按鍵重開一條連線，且每一次都要等滿命令逾時。使用者看到的是打字卡住。
    /// </remarks>
    [Fact]
    public async Task 退避期間不再重開連線()
    {
        var source = new FailingConnectionSource();
        var catalog = new SqlMetadataCatalog(
            source,
            TimeSpan.FromMinutes(5),
            failureBackoff: TimeSpan.FromMinutes(5));

        await catalog.GetSnapshotAsync(CancellationToken.None);
        await catalog.GetSnapshotAsync(CancellationToken.None);
        await catalog.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(1, source.Attempts);
    }

    /// <summary>
    /// 退避只是延後，不是放棄：連線恢復之後不必重開查詢視窗。
    /// </summary>
    [Fact]
    public async Task 退避結束後會再試一次()
    {
        var source = new FailingConnectionSource();
        var catalog = new SqlMetadataCatalog(
            source,
            TimeSpan.FromMinutes(5),
            failureBackoff: TimeSpan.Zero);

        await catalog.GetSnapshotAsync(CancellationToken.None);
        await catalog.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(2, source.Attempts);
    }

    /// <summary>
    /// 按重新整理的人就是在說「我修好了，現在再試一次」。
    /// </summary>
    [Fact]
    public async Task 重新整理會清掉退避()
    {
        var source = new FailingConnectionSource();
        var catalog = new SqlMetadataCatalog(
            source,
            TimeSpan.FromMinutes(5),
            failureBackoff: TimeSpan.FromMinutes(5));

        await catalog.GetSnapshotAsync(CancellationToken.None);
        catalog.Invalidate();
        await catalog.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(2, source.Attempts);
    }

    /// <summary>
    /// 契約違反是程式錯誤，必須一路浮到平台邊界去留下完整堆疊。
    /// </summary>
    [Fact]
    public async Task 參數違約仍然擲出例外()
    {
        var catalog = CreateCatalog();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => catalog.GetDetailAsync(null!, CancellationToken.None));
    }

    private static SqlMetadataCatalog CreateCatalog() =>
        new(new FailingConnectionSource(), TimeSpan.FromMinutes(5));

    private sealed class FailingConnectionSource : ISqlConnectionSource
    {
        public string CacheKey => "server-a|db1";

        public string ServerCacheKey => "server-a";

        public string DatabaseName => "db1";

        /// <summary>開過幾次連線；用來確認失敗沒有被當成結果快取起來。</summary>
        public int Attempts { get; private set; }

        public IDbConnection OpenConnection()
        {
            Attempts++;
            throw new UnreachableServerException();
        }
    }

    /// <summary><see cref="DbException"/> 是抽象的，測試要自己給一個具體型別。</summary>
    private sealed class UnreachableServerException : DbException
    {
        public UnreachableServerException()
            : base("連不上伺服器。")
        {
        }
    }
}
