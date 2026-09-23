using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Search;
using SqlAssist.Ssms22.Search;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Search;

/// <summary>
/// 範圍那顆下拉的資料庫清單：問一次、留一份，換連線與問不到各有自己的規則。
/// </summary>
/// <remarks>
/// 這一份不驗伺服器回什麼——那是 <c>SqlCatalogSearchDatabases</c> 與它的查詢在驗的事。
/// 這裡只問「送了幾條查詢」與「哪一份留在手上」，而那兩件正是使用者看得到的症狀。
/// </remarks>
public sealed class SqlSearchScopeDatabasesTests
{
    [Fact]
    public async Task 展開兩次只問一條查詢()
    {
        var calls = 0;
        using var scope = new SqlSearchScopeDatabases((_, _) =>
        {
            calls++;
            return List("Library", "LibArchive");
        });

        var catalog = Catalog("Library");

        Assert.Equal(new[] { "Library", "LibArchive" }, Names(await scope.EnsureAsync(catalog)));
        Assert.Equal(new[] { "Library", "LibArchive" }, Names(await scope.EnsureAsync(catalog)));

        // 每展開一次就問一輪的代價是一條連線；清單在同一條連線上不會自己變。
        Assert.Equal(1, calls);
        Assert.True(scope.IsLoaded);
        Assert.False(scope.IsUnavailable);
    }

    [Fact]
    public async Task 同一輪還在飛時再展開不送第二條查詢()
    {
        var gate = new TaskCompletionSource<bool>();
        var calls = 0;
        using var scope = new SqlSearchScopeDatabases((_, _) =>
        {
            calls++;
            gate.Task.GetAwaiter().GetResult();
            return List("Library");
        });

        var catalog = Catalog("Library");
        var first = scope.EnsureAsync(catalog);
        var second = scope.EnsureAsync(catalog);

        Assert.True(scope.IsLoading);
        gate.SetResult(true);

        Assert.Equal(new[] { "Library" }, Names(await first));
        Assert.Equal(new[] { "Library" }, Names(await second));
        // 兩條一模一樣的查詢只是把同一份答案讀兩遍，而晚回來的那一條會讓清單閃一次。
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 換一台就重問而且不留上一台的名稱()
    {
        var databases = new Queue<IReadOnlyList<SqlCatalogSearchDatabase>?>(new[]
        {
            List("Library"),
            List("LibReporting")
        });
        using var scope = new SqlSearchScopeDatabases((_, _) => databases.Dequeue());

        Assert.Equal(new[] { "Library" }, Names(await scope.EnsureAsync(Catalog("Library"))));

        // 資料庫名稱是每一台伺服器自己的；留著上一台那一份，使用者會勾到一個那裡不存在的名稱。
        Assert.Equal(new[] { "LibReporting" }, Names(await scope.EnsureAsync(Catalog("LibReporting", server: "LIBSQL02"))));
        Assert.Empty(databases);
    }

    /// <summary>
    /// 換一台之後，<b>還沒問</b>之前手上那一份就已經不算數。
    /// </summary>
    /// <remarks>
    /// 宿主在面板展開時會先畫手上這一份（已經勾起來的條件必須看得見），只有在
    /// <see cref="SqlSearchScopeDatabases.IsLoaded"/> 是 false 時才去重問。比對只寫在
    /// <c>EnsureAsync</c> 裡的那一版，換一台之後 IsLoaded 仍是上一台的 true，
    /// 宿主據此提早收工——下拉從此畫著上一台的資料庫，而且不會自己好。
    /// </remarks>
    [Fact]
    public async Task 換一台之後手上那一份當場作廢而不是等到重問才換()
    {
        using var scope = new SqlSearchScopeDatabases((_, _) => List("Library"));

        await scope.EnsureAsync(Catalog("Library"));
        Assert.True(scope.IsLoaded);

        scope.SyncTo(Catalog("LibReporting", server: "LIBSQL02"));
        Assert.False(scope.IsLoaded);
        Assert.Empty(scope.Items);
        Assert.Equal("", scope.CurrentName);

        // 沒有連線不算換一台：切到沒有連線的查詢視窗時清掉清單，回來還要再付一條查詢。
        await scope.EnsureAsync(Catalog("LibReporting", server: "LIBSQL02"));
        scope.SyncTo(null);
        Assert.True(scope.IsLoaded);
    }

    /// <remarks>
    /// 名稱由開啟後的連線說了算：物件總管那條連線的連線物件上沒有初始目錄，
    /// 而範圍摘要一定要說得出目標。
    /// </remarks>
    [Fact]
    public async Task 清單回來時記下沒有指名時搜的是哪一個()
    {
        using var scope = new SqlSearchScopeDatabases((_, _) => new[]
        {
            new SqlCatalogSearchDatabase("Library", isSystem: false),
            new SqlCatalogSearchDatabase("master", isSystem: true, isCurrent: true)
        });

        Assert.Equal("", scope.CurrentName);
        await scope.EnsureAsync(Catalog("Library"));
        Assert.Equal("master", scope.CurrentName);
    }

    [Fact]
    public async Task 問不到時留住上一份並且下一次展開再試()
    {
        var results = new Queue<IReadOnlyList<SqlCatalogSearchDatabase>?>(new[]
        {
            List("Library", "LibArchive"),
            null,
            List("Library", "LibArchive", "LibReporting")
        });
        using var scope = new SqlSearchScopeDatabases((_, _) => results.Dequeue());

        var catalog = Catalog("Library");
        Assert.Equal(2, (await scope.EnsureAsync(catalog)).Count);

        scope.Invalidate();
        // 清空等於說「這台伺服器上一個都進不去」，而那與「這一次問不到」是兩件事。
        Assert.Equal(2, (await scope.EnsureAsync(catalog)).Count);
        Assert.True(scope.IsUnavailable);
        Assert.False(scope.IsLoaded);

        // 問不到不算問過：下一次展開要再試，否則使用者得關掉整個工具窗才問得到清單。
        Assert.Equal(3, (await scope.EnsureAsync(catalog)).Count);
        Assert.False(scope.IsUnavailable);
        Assert.True(scope.IsLoaded);
    }

    [Fact]
    public async Task 重新整理之後重問()
    {
        var calls = 0;
        using var scope = new SqlSearchScopeDatabases((_, _) =>
        {
            calls++;
            return List("Library");
        });

        var catalog = Catalog("Library");
        await scope.EnsureAsync(catalog);
        scope.Invalidate();
        await scope.EnsureAsync(catalog);

        // 剛建好的資料庫不在上一次那一份裡，而那正是使用者按重新整理的理由。
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task 沒有目錄時不問也不清掉手上那一份()
    {
        var calls = 0;
        using var scope = new SqlSearchScopeDatabases((_, _) =>
        {
            calls++;
            return List("Library");
        });

        Assert.Empty(await scope.EnsureAsync(catalog: null));
        Assert.Equal(0, calls);

        await scope.EnsureAsync(Catalog("Library"));
        // 切到沒有連線的查詢視窗不該把清單擦掉：回來時它還是對的，而重問要再付一條查詢。
        Assert.Equal(new[] { "Library" }, Names(await scope.EnsureAsync(catalog: null)));
        Assert.Equal(1, calls);
    }

    private static SqlMetadataCatalog Catalog(string database, string server = "LIBSQL01") =>
        SqlSearchTestCatalogs.Create(database, server);

    private static IReadOnlyList<SqlCatalogSearchDatabase> List(params string[] names)
    {
        var databases = new List<SqlCatalogSearchDatabase>(names.Length);
        foreach (var name in names) databases.Add(new SqlCatalogSearchDatabase(name, isSystem: false));
        return databases;
    }

    private static string[] Names(IReadOnlyList<SqlCatalogSearchDatabase> databases)
    {
        var names = new string[databases.Count];
        for (var index = 0; index < databases.Count; index++) names[index] = databases[index].Name;
        return names;
    }
}
