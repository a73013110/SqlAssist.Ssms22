using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Search;
using Xunit;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// 一個資料庫的全量索引：撈回來的東西、版本戳，以及資料庫說不行時的降級。
/// </summary>
public sealed class SqlCatalogSearchIndexTests
{
    private static readonly DateTime Earlier = new(2025, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2025, 6, 2, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void 一次連線把物件資料行與結構描述都撈回來()
    {
        var server = NewServer();

        var index = SqlCatalogSearchIndex.TryBuild(server.SourceFor("Library"), CancellationToken.None);

        Assert.NotNull(index);
        Assert.Equal("Library", index!.DatabaseName);
        Assert.Equal(new[] { "Lib_Reader", "Loan", "Lib_Tag" }, index.Objects.Select(entry => entry.Info.Name));
        Assert.Equal(new[] { "PUBL_CODE", "CopyNo" }, index.Columns.Select(column => column.Name));
        Assert.Equal(new[] { "dbo" }, index.Schemas);

        // 三條查詢共用同一條連線：分開等於每建一份索引多開兩次連線。
        Assert.Equal(1, server.Opened);
        Assert.Equal(3, server.Commands.Count);
    }

    /// <summary>物件記得自己從哪個資料庫來；<c>object_id</c> 只在那裡唯一。</summary>
    [Fact]
    public void 物件帶著自己的資料庫名稱()
    {
        var index = SqlCatalogSearchIndex.TryBuild(NewServer().SourceFor("Library"), CancellationToken.None);

        Assert.All(index!.Objects, entry => Assert.Equal("Library", entry.Info.DatabaseName));
        Assert.All(index.Columns, column => Assert.Equal("Library", column.Owner.DatabaseName));
    }

    /// <remarks>
    /// 增量更新要的就是這兩個值。只看時間戳分不出「什麼都沒變」與「剛好卸除了
    /// 最後改過的那一個」——後者的最大值會倒退，而倒退看起來與沒變一樣。
    /// </remarks>
    [Fact]
    public void 記下最大的修改時間與物件數()
    {
        var index = SqlCatalogSearchIndex.TryBuild(NewServer().SourceFor("Library"), CancellationToken.None);

        Assert.Equal(Later, index!.ModifiedThrough);
        Assert.Equal(3, index.ObjectCount);
    }

    /// <summary>沒有任何一列帶時間戳時是 null，不是一個猜出來的值。</summary>
    [Fact]
    public void 沒有物件就沒有版本戳()
    {
        var server = new FakeCatalogServer();
        server.Add("Library");

        var index = SqlCatalogSearchIndex.TryBuild(server.SourceFor("Library"), CancellationToken.None);

        Assert.Null(index!.ModifiedThrough);
        Assert.Equal(0, index.ObjectCount);
    }

    /// <summary>認不得的型別代碼整筆丟掉：它沒有分類可以掛。</summary>
    [Fact]
    public void 認不得的型別不進索引()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan", "U")
            .WithObject(2, "dbo", "FK_Loan_Copy", "F");

        var index = SqlCatalogSearchIndex.TryBuild(server.SourceFor("Library"), CancellationToken.None);

        var entry = Assert.Single(index!.Objects);
        Assert.Equal("Loan", entry.Info.Name);
    }

    /// <remarks>
    /// 對不上物件清單的資料行是系統內部物件的，或是兩條查詢之間剛建起來的。
    /// 掛一筆「不知道屬於誰」的資料行上去，畫面上會出現一列沒有位置的結果。
    /// </remarks>
    [Fact]
    public void 對不上物件的資料行丟掉()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan", "U")
            .WithColumn(1, "CopyNo")
            .WithColumn(99, "Branch");

        var index = SqlCatalogSearchIndex.TryBuild(server.SourceFor("Library"), CancellationToken.None);

        var column = Assert.Single(index!.Columns);
        Assert.Equal("CopyNo", column.Name);
        Assert.Equal("Loan", column.Owner.Name);
    }

    /// <summary>
    /// 定義本文超過位元組上限之後只留名稱，並且說出來。
    /// </summary>
    /// <remarks>
    /// 安靜地少一半結果是最糟的：使用者會以為那個字串在這個資料庫裡不存在。
    /// 上限算的是留下來的位元組，一個 UTF-16 字元兩個位元組。
    /// </remarks>
    [Fact]
    public void 定義本文超過上限就只留名稱並標記()
    {
        var first = new string('A', 20);
        var second = new string('B', 20);
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Lib_Reader", "V", first)
            .WithObject(2, "dbo", "Lib_Tag", "V", second);

        var index = SqlCatalogSearchIndex.TryBuild(
            server.SourceFor("Library"), CancellationToken.None, maxDefinitionBytes: 40);

        Assert.False(index!.HasCompleteDefinitions);
        Assert.Equal(first, index.Objects[0].Definition);

        // 名稱照收，只有本文停。
        Assert.Null(index.Objects[1].Definition);
        Assert.Equal("Lib_Tag", index.Objects[1].Info.Name);
    }

    [Fact]
    public void 定義本文放得下時索引是完整的()
    {
        var index = SqlCatalogSearchIndex.TryBuild(NewServer().SourceFor("Library"), CancellationToken.None);

        Assert.True(index!.HasCompleteDefinitions);
    }

    /// <summary>
    /// 連不上時回 null，不是擲例外。
    /// </summary>
    /// <remarks>
    /// 冒出去會落在 Ssms22 的平台邊界上，而它把每一次都記成一份完整堆疊——
    /// 連線斷掉時使用者每打一個字就失敗一次。
    /// </remarks>
    [Fact]
    public void 連不上時回傳null而不是擲例外()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").FailsOnOpen = true;

        Assert.Null(SqlCatalogSearchIndex.TryBuild(server.SourceFor("Library"), CancellationToken.None));
    }

    /// <summary>單一條查詢失敗也是整份降級，不是回半份索引。</summary>
    [Fact]
    public void 其中一條查詢失敗就整份降級()
    {
        var server = NewServer();
        server.Find("Library").FailsOnQueryContaining = "FROM sys.columns";

        Assert.Null(SqlCatalogSearchIndex.TryBuild(server.SourceFor("Library"), CancellationToken.None));
    }

    /// <summary>
    /// 降級不等於一個字都不留：哪一條查詢、哪一個資料庫、伺服器說了什麼。
    /// </summary>
    /// <remarks>
    /// 「連線斷了」與「這條查詢寫錯了」在畫面上長得一模一樣，唯一分得出來的
    /// 資訊正是被吃掉的那句話。
    /// </remarks>
    [Fact]
    public void 失敗會把哪一條查詢與伺服器說的話送出去()
    {
        var server = NewServer();
        server.Find("Library").FailsOnQueryContaining = "FROM sys.columns";

        var line = Assert.Single(Capture(() =>
            SqlCatalogSearchIndex.TryBuild(server.SourceFor("Library"), CancellationToken.None)));

        Assert.Contains("資料行", line);
        Assert.Contains("Library", line);
        Assert.Contains("連不上伺服器。", line);
    }

    /// <summary>參數契約違反是程式錯誤，要一路浮到平台邊界去留下完整堆疊。</summary>
    [Fact]
    public void 參數違約仍然擲出例外()
    {
        Assert.Throws<ArgumentNullException>(
            () => SqlCatalogSearchIndex.TryBuild(null!, CancellationToken.None));
    }

    /// <summary>取消不被當成資料庫失敗吞掉。</summary>
    [Fact]
    public void 取消會擲出取消例外()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => SqlCatalogSearchIndex.TryBuild(NewServer().SourceFor("Library"), cancellation.Token));
    }

    internal static List<string> Capture(Action action)
    {
        var reported = new List<string>();
        var previous = SqlMetadataFailure.Reporter;
        SqlMetadataFailure.Reporter = (operation, exception) => reported.Add(operation + "｜" + exception.Message);

        try
        {
            action();
        }
        finally
        {
            SqlMetadataFailure.Reporter = previous;
        }

        return reported;
    }

    /// <summary>圖書館領域的一小份目錄；公開 repo 不放真實系統的名稱。</summary>
    internal static FakeCatalogServer NewServer()
    {
        var server = new FakeCatalogServer();

        server.Add("Library")
            .WithObject(1, "dbo", "Lib_Reader", "U", modifiedAt: Earlier)
            .WithObject(2, "dbo", "Loan", "U", modifiedAt: Later)
            .WithObject(3, "dbo", "Lib_Tag", "V", "SELECT * FROM dbo.Loan;", Earlier)
            .WithColumn(1, "PUBL_CODE")
            .WithColumn(2, "CopyNo")
            .WithSchema("dbo");

        return server;
    }
}
