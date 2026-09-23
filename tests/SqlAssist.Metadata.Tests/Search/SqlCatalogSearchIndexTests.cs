using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Search;
using Xunit;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// 一個資料庫的全量索引：兩段各自撈回來的東西、版本戳、增量重新整理，以及資料庫說不行時的降級。
/// </summary>
[Collection(MetadataFailureCollection.Name)]
public sealed class SqlCatalogSearchIndexTests
{
    private static readonly DateTime Earlier = new(2025, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2025, 6, 2, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Latest = new(2025, 9, 4, 11, 15, 0, DateTimeKind.Utc);

    [Fact]
    public void 一次連線把兩段都撈回來()
    {
        var server = NewServer();

        var index = Build(server, includeDefinitions: true);

        Assert.NotNull(index);
        Assert.Equal("Library", index!.DatabaseName);
        Assert.Equal(new[] { "Lib_Reader", "Loan", "Lib_Tag" }, index.Objects.Select(info => info.Name));
        Assert.Equal(new[] { "PUBL_CODE", "CopyNo" }, index.Columns.Select(column => column.Name));
        Assert.Equal(new[] { "dbo" }, index.Schemas);
        Assert.Equal("SELECT * FROM dbo.Loan;", index.Definitions!.For(3));

        // 四條查詢共用同一條連線：分開等於每建一份索引多開三次連線。
        Assert.Equal(1, server.Opened);
        Assert.Equal(4, server.Commands.Count);
    }

    /// <summary>
    /// 不搜定義本文的那一輪，第二段查詢<b>連送都不送</b>。
    /// </summary>
    /// <remarks>
    /// 撈回來再丟掉的話，第一次搜尋最貴的那一段一毫秒都沒有省到，而使用者以為自己關掉了它。
    /// 分得出兩者的證據只有「那一條查詢有沒有被執行」，不是結果筆數。
    /// </remarks>
    [Fact]
    public void 不搜定義本文時不送第二段查詢()
    {
        var server = NewServer();

        var index = Build(server, includeDefinitions: false);

        Assert.Null(index!.Definitions);
        Assert.Equal(0, server.CountCommands("sys.sql_modules"));
        Assert.Equal(3, server.Commands.Count);

        // 名稱與資料行照樣完整：省掉的只有本文那一段。
        Assert.Equal(3, index.ObjectCount);
        Assert.Equal(2, index.Columns.Count);
    }

    /// <summary>之後才要本文時只補第二段，第一段不重掃。</summary>
    [Fact]
    public void 補上第二段不重掃第一段()
    {
        var server = NewServer();
        var first = Build(server, includeDefinitions: false);
        server.Commands.Clear();

        var second = first!.TryAddDefinitions(server.SourceFor("Library"), CancellationToken.None);

        Assert.Equal("SELECT * FROM dbo.Loan;", second!.Definitions!.For(3));
        Assert.Equal(1, server.CountCommands("sys.sql_modules"));
        Assert.Single(server.Commands);

        // 第一段原樣沿用，不重新配置。
        Assert.Same(first.Objects, second.Objects);
        Assert.Same(first.Columns, second.Columns);

        // 不就地改寫：同一份索引會被好幾條搜尋執行緒同時讀。
        Assert.Null(first.Definitions);
    }

    /// <summary>物件記得自己從哪個資料庫來；<c>object_id</c> 只在那裡唯一。</summary>
    [Fact]
    public void 物件帶著自己的資料庫名稱()
    {
        var index = Build(NewServer(), includeDefinitions: true);

        Assert.All(index!.Objects, info => Assert.Equal("Library", info.DatabaseName));
        Assert.All(index.Columns, column => Assert.Equal("Library", column.Owner.DatabaseName));
    }

    /// <remarks>
    /// 增量更新要的就是這兩個值。只看時間戳分不出「什麼都沒變」與「剛好卸除了
    /// 最後改過的那一個」——後者的最大值會倒退，而倒退看起來與沒變一樣。
    /// </remarks>
    [Fact]
    public void 記下最大的修改時間與物件數()
    {
        var index = Build(NewServer(), includeDefinitions: true);

        Assert.Equal(Later, index!.ModifiedThrough);
        Assert.Equal(3, index.ObjectCount);
    }

    /// <summary>沒有任何一列帶時間戳時是 null，不是一個猜出來的值。</summary>
    [Fact]
    public void 沒有物件就沒有版本戳()
    {
        var server = new FakeCatalogServer();
        server.Add("Library");

        var index = Build(server, includeDefinitions: true);

        Assert.Null(index!.ModifiedThrough);
        Assert.Equal(0, index.ObjectCount);

        // 問過了、真的沒有，與「還沒問」是兩件事。
        Assert.NotNull(index.Definitions);
        Assert.Equal(0, index.Definitions!.Count);
    }

    /// <summary>認不得的型別代碼整筆丟掉：它沒有分類可以掛。</summary>
    [Fact]
    public void 認不得的型別不進索引()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan", "U")
            .WithObject(2, "dbo", "Lib_Queue", "SQ");

        var index = Build(server, includeDefinitions: true);

        var info = Assert.Single(index!.Objects);
        Assert.Equal("Loan", info.Name);
    }

    /// <summary>
    /// 條件約束四種都進索引，而 <c>CHECK</c> 與 <c>DEFAULT</c> 還帶著自己的運算式。
    /// </summary>
    /// <remarks>
    /// 主索引鍵與外來鍵沒有運算式，所以只有名稱命中；把它們也算成「有本文」的話，
    /// 本文那一段會多掃一批永遠比不中的候選。
    /// </remarks>
    [Fact]
    public void 條件約束進索引並帶著運算式()
    {
        var server = new FakeCatalogServer();
        server.Add("Library")
            .WithObject(1, "dbo", "Loan", "U", modifiedAt: Earlier)
            .WithObject(11, "dbo", "CK_Loan_CopyNo", "C", "([CopyNo]>(0))", Earlier)
            .WithObject(12, "dbo", "DF_Loan_CopyNo", "D", "((0))", Earlier)
            .WithObject(13, "dbo", "PK_Loan", "PK", modifiedAt: Earlier)
            .WithObject(14, "dbo", "FK_Loan_Copy", "F", modifiedAt: Earlier);

        var index = Build(server, includeDefinitions: true);

        Assert.Equal(
            new[] { "Loan", "CK_Loan_CopyNo", "DF_Loan_CopyNo", "PK_Loan", "FK_Loan_Copy" },
            index!.Objects.Select(info => info.Name));
        Assert.All(
            index.Objects.Where(info => info.Name != "Loan"),
            info => Assert.Equal(SqlObjectKind.Constraint, info.Kind));

        Assert.Equal("([CopyNo]>(0))", index.Definitions!.For(11));
        Assert.Equal("((0))", index.Definitions.For(12));
        Assert.Null(index.Definitions.For(13));
        Assert.Null(index.Definitions.For(14));
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

        var index = Build(server, includeDefinitions: true);

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
            server.SourceFor("Library"), includeDefinitions: true, CancellationToken.None, out _,
            maxDefinitionBytes: 40);

        Assert.False(index!.Definitions!.IsComplete);
        Assert.Equal(first, index.Definitions.For(1));

        // 名稱照收，只有本文停。
        Assert.Null(index.Definitions.For(2));
        Assert.Equal("Lib_Tag", index.Objects[1].Name);
    }

    [Fact]
    public void 定義本文放得下時索引是完整的()
    {
        var index = Build(NewServer(), includeDefinitions: true);

        Assert.True(index!.Definitions!.IsComplete);
    }

    /// <summary>位元組估計跟著定義本文長大；快取的預算靠它淘汰。</summary>
    /// <remarks>
    /// 照份數算上限的症狀是四個大庫就把行程撐爆，而四個小庫又白白丟掉留得住的東西。
    /// </remarks>
    [Fact]
    public void 位元組估計把定義本文算進去()
    {
        var withoutText = Build(NewTagServer(), includeDefinitions: false);
        var withText = Build(NewTagServer(), includeDefinitions: true);

        Assert.Equal(4096 * sizeof(char), withText!.ApproximateBytes - withoutText!.ApproximateBytes);

        static FakeCatalogServer NewTagServer()
        {
            var server = new FakeCatalogServer();
            server.Add("Library").WithObject(1, "dbo", "Lib_Tag", "V", new string('A', 4096));
            return server;
        }
    }

    /// <summary>
    /// 重新整理只撈變更過的物件。
    /// </summary>
    /// <remarks>
    /// 分得出「真的增量」與「號稱增量」的只有一件事：沒有變更過的那一份，即使伺服器上的
    /// 內容已經不一樣了，重新整理後仍然是舊的。結果筆數兩邊一模一樣。
    /// </remarks>
    [Fact]
    public void 重新整理只撈變更過的物件()
    {
        var server = new FakeCatalogServer();
        var database = server.Add("Library")
            .WithObject(1, "dbo", "Lib_Tag", "V", "SELECT CopyNo FROM dbo.Loan;", Earlier)
            .WithObject(2, "dbo", "Lib_Reader", "V", "SELECT PUBL_CODE FROM dbo.Copy;", Later)
            .WithColumn(1, "CopyNo")
            .WithSchema("dbo");

        var first = Build(server, includeDefinitions: true);

        // Lib_Tag 沒有動過時間戳，所以它不該被重撈；Lib_Reader 的時間戳推到最新。
        database.Rewrite(1, "這一份不應該出現在重新整理之後的索引裡。");
        database.Touch(2, "SELECT Branch FROM dbo.Copy;", Latest);

        var refreshed = SqlCatalogSearchIndex.TryRefresh(
            first!, server.SourceFor("Library"), includeDefinitions: true, CancellationToken.None);

        Assert.Equal("SELECT CopyNo FROM dbo.Loan;", refreshed!.Definitions!.For(1));
        Assert.Equal("SELECT Branch FROM dbo.Copy;", refreshed.Definitions.For(2));
        Assert.Equal(Latest, refreshed.ModifiedThrough);

        // 沒有變更過的物件，它的資料行也一起沿用。
        Assert.Equal(new[] { "CopyNo" }, refreshed.Columns.Select(column => column.Name));
    }

    /// <summary>
    /// 物件那一條整份重撈，所以卸除看得出來。
    /// </summary>
    /// <remarks>
    /// 卸除不會留下任何時間戳。物件那一條也走增量的話，被砍掉的那一張表會永遠留在索引上，
    /// 而使用者點下去得到的是「取不到結構」。
    /// </remarks>
    [Fact]
    public void 重新整理看得出物件被卸除()
    {
        var server = new FakeCatalogServer();
        var database = server.Add("Library")
            .WithObject(1, "dbo", "Lib_Tag", "V", "SELECT 1;", Earlier)
            .WithObject(2, "dbo", "Loan", "U", modifiedAt: Later)
            .WithColumn(2, "CopyNo")
            .WithSchema("dbo");

        var first = Build(server, includeDefinitions: true);
        database.Drop(2);

        var refreshed = SqlCatalogSearchIndex.TryRefresh(
            first!, server.SourceFor("Library"), includeDefinitions: true, CancellationToken.None);

        Assert.Equal(new[] { "Lib_Tag" }, refreshed!.Objects.Select(info => info.Name));
        Assert.Empty(refreshed.Columns);
    }

    /// <summary>
    /// 有物件說不出自己的時間戳時整份重撈。
    /// </summary>
    /// <remarks>
    /// 增量查詢撈不到沒有時間戳的那幾列，沿用上一份等於它們的資料行與定義本文永遠不出現，
    /// 而畫面上看不出少了什麼。寧可多付一次。
    /// </remarks>
    [Fact]
    public void 有物件沒有時間戳時整份重撈()
    {
        var server = new FakeCatalogServer();
        var database = server.Add("Library")
            .WithObject(1, "dbo", "Lib_Tag", "V", "SELECT 1;", Earlier)
            .WithObject(2, "dbo", "Lib_Reader", "V", "SELECT 2;")
            .WithSchema("dbo");

        var first = Build(server, includeDefinitions: true);
        database.Rewrite(1, "這一份應該被重撈回來。");

        var refreshed = SqlCatalogSearchIndex.TryRefresh(
            first!, server.SourceFor("Library"), includeDefinitions: true, CancellationToken.None);

        Assert.Equal("這一份應該被重撈回來。", refreshed!.Definitions!.For(1));
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

        Assert.Null(Build(server, includeDefinitions: true));
    }

    /// <summary>單一條查詢失敗也是整份降級，不是回半份索引。</summary>
    [Fact]
    public void 其中一條查詢失敗就整份降級()
    {
        var server = NewServer();
        server.Find("Library").FailsOnQueryContaining = "FROM sys.columns";

        Assert.Null(Build(server, includeDefinitions: true));
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
        server.Find("Library").FailsOnQueryContaining = "sys.sql_modules";

        var line = Assert.Single(Capture(() => Build(server, includeDefinitions: true)));

        Assert.Contains("定義本文", line);
        Assert.Contains("Library", line);
        Assert.Contains("連不上伺服器。", line);
    }

    /// <summary>參數契約違反是程式錯誤，要一路浮到平台邊界去留下完整堆疊。</summary>
    [Fact]
    public void 參數違約仍然擲出例外()
    {
        Assert.Throws<ArgumentNullException>(
            () => SqlCatalogSearchIndex.TryBuild(null!, includeDefinitions: true, CancellationToken.None));
    }

    /// <summary>取消不被當成資料庫失敗吞掉。</summary>
    [Fact]
    public void 取消會擲出取消例外()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => SqlCatalogSearchIndex.TryBuild(
                NewServer().SourceFor("Library"), includeDefinitions: true, cancellation.Token));
    }

    /// <summary>資料庫清單帶著「是不是系統資料庫」，而且不建任何索引。</summary>
    /// <remarks>
    /// 打開下拉就先索引所有進得去的資料庫是禁止的。這一條只回答「有哪些可以選」。
    /// </remarks>
    [Fact]
    public void 資料庫清單帶著系統旗標而且不建索引()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").WithObject(1, "dbo", "Loan", "U");
        server.Add("msdb", isSystem: true);

        var databases = SqlCatalogSearchDatabases.TryList(server.SourceFor("Library"), CancellationToken.None);

        Assert.NotNull(databases);
        Assert.Equal(new[] { "Library", "msdb" }, databases!.Select(database => database.Name));
        Assert.Equal(new[] { false, true }, databases.Select(database => database.IsSystem));

        // 只問了清單那一條，一個物件都沒有掃。
        Assert.Single(server.Commands);
        Assert.Equal(0, server.CountCommands("sys.sql_modules"));
    }

    [Fact]
    public void 資料庫清單連不上時回傳null()
    {
        var server = new FakeCatalogServer();
        server.Add("Library").FailsOnOpen = true;

        Assert.Null(SqlCatalogSearchDatabases.TryList(server.SourceFor("Library"), CancellationToken.None));
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

    /// <summary>
    /// 連線字串上沒有初始目錄時，資料庫名稱由開啟後的連線說了算。
    /// </summary>
    /// <remarks>
    /// 物件總管上那一條連線沒有初始目錄，來源交出來的 <c>DatabaseName</c> 是空字串，而索引的
    /// 建構子不收空名稱。照來源那一份走的版本會擲 <see cref="ArgumentException"/>，而它不是
    /// <see cref="System.Data.Common.DbException"/>，這一層的降級接不住——使用者看到的是
    /// 「『catalog』這一輪失敗：資料庫名稱不可為空。參數名稱: databaseName」。
    /// </remarks>
    [Fact]
    public void 連線沒有初始目錄時索引記的是開起來那一個資料庫()
    {
        var server = NewServer();

        var index = SqlCatalogSearchIndex.TryBuild(
            server.SourceWithoutInitialCatalog("Library"), includeDefinitions: true, CancellationToken.None);

        Assert.NotNull(index);
        Assert.Equal("Library", index!.DatabaseName);
        Assert.Equal(new[] { "Lib_Reader", "Loan", "Lib_Tag" }, index.Objects.Select(info => info.Name));
    }

    private static SqlCatalogSearchIndex? Build(FakeCatalogServer server, bool includeDefinitions) =>
        SqlCatalogSearchIndex.TryBuild(server.SourceFor("Library"), includeDefinitions, CancellationToken.None);

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
