using System;
using System.Linq;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Model;

public sealed class SqlObjectLookupTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Lib_Reader", "Lib_Reader")]
    [InlineData("SELECT * FROM [dbo].[Lib_Reader]", "Lib_Reader")]
    [InlineData("SELECT * FROM \"dbo\".\"Lib_Reader\"", "Lib_Reader")]
    [InlineData("SELECT r FROM dbo.Lib_Reader r", "r FROM")]
    public void 同一份語法分析在冷啟動與清除後都能重新辨識(string sql, string hover)
    {
        var lookup = Assert.IsType<SqlObjectLookup>(SqlObjectLookup.Create(sql, sql.IndexOf(hover, StringComparison.Ordinal)));
        Assert.Null(lookup.FindCandidate(null));
        Assert.Null(lookup.FindCandidate(SqlDatabaseSnapshot.Empty));

        var before = Table(1, "Library");
        Assert.Same(before, lookup.FindCandidate(Snapshot(before))!.Object);

        // 文字完全不變；清快取及物件重建不能沿用舊 object_id 或負面結果。
        Assert.Null(lookup.FindCandidate(SqlDatabaseSnapshot.Empty));
        var after = Table(2, "Library");
        Assert.Same(after, lookup.FindCandidate(Snapshot(after))!.Object);
    }

    [Fact]
    public void 同名同編號物件切換資料庫後採用新快照()
    {
        const string sql = "SELECT * FROM dbo.Lib_Reader";
        var lookup = SqlObjectLookup.Create(sql, sql.Length - 1)!;
        var before = Table(1, "Library");
        var after = Table(1, "LibArchive");

        Assert.Same(before, lookup.FindCandidate(Snapshot(before))!.Object);
        Assert.Same(after, lookup.FindCandidate(Snapshot(after))!.Object);
    }

    [Fact]
    public void 欄位明細背景載回與刷新不需要變更文字()
    {
        const string sql = "SELECT r.ReaderId FROM dbo.Lib_Reader r";
        var lookup = SqlObjectLookup.Create(sql, sql.IndexOf("ReaderId", StringComparison.Ordinal))!;
        var table = Table(1, "Library");
        var candidate = lookup.FindCandidate(Snapshot(table))!;
        Assert.True(candidate.NeedsColumn);
        Assert.Null(lookup.Locate(candidate)!.Column);

        var column = new SqlColumnInfo(1, "ReaderId", "int", false);
        Assert.Same(column, lookup.Locate(candidate, new SqlObjectDetail(table, new[] { column }))!.Column);

        var updated = new SqlColumnInfo(1, "ReaderId", "bigint", true);
        Assert.Same(updated, lookup.Locate(candidate, new SqlObjectDetail(table, new[] { updated }))!.Column);
        Assert.Null(lookup.Locate(candidate, new SqlObjectDetail(table)));
    }

    [Fact]
    public void 真正查無物件之後新增也能辨識()
    {
        const string sql = "SELECT * FROM dbo.Lib_Reader";
        var lookup = SqlObjectLookup.Create(sql, sql.Length - 1)!;
        var missing = new SqlDatabaseSnapshot("Library", Array.Empty<SqlObjectInfo>(), new[] { "dbo" },
            Array.Empty<string>(), DateTimeOffset.UtcNow);

        Assert.Null(lookup.FindCandidate(missing));
        Assert.NotNull(lookup.FindCandidate(Snapshot(Table(1, "Library"))));
    }

    [Theory]
    [InlineData("SELECT * FROM LibArchive.other.Lib_Reader")]
    [InlineData("SELECT * FROM LibMirror.LibArchive.other.Lib_Reader")]
    public void 跨庫限定詞不退回其他結構描述的同名物件(string sql)
    {
        var lookup = SqlObjectLookup.Create(sql, sql.Length - 1)!;
        Assert.Null(lookup.FindCandidate(Snapshot(Table(1, "LibArchive"))));
    }

    /// <summary>
    /// 指令碼自己宣告的名稱不必等連線，也不必等快取。
    /// </summary>
    /// <remarks>
    /// 中繼資料對這三種一列都查不到——暫存資料表在 tempdb 裡、資料表變數不是
    /// <c>sys.objects</c> 裡的物件、CTE 只存在於這份指令碼裡。只問快照的症狀是
    /// 滑鼠停上去什麼都沒有，而使用者上一行才剛把它寫出來。
    /// </remarks>
    [Theory]
    [InlineData(
        "CREATE TABLE #TempTest (ID INT, Name NVARCHAR(50)); SELECT * FROM #TempTest",
        "#TempTest",
        SqlObjectKind.TemporaryTable)]
    [InlineData(
        "DECLARE @rows TABLE (ID INT, Name NVARCHAR(50)); SELECT * FROM @rows",
        "@rows",
        SqlObjectKind.TableVariable)]
    [InlineData(
        ";WITH c AS (SELECT ID, Name FROM dbo.Lib_Reader) SELECT * FROM c",
        "c",
        SqlObjectKind.CommonTableExpression)]
    public void 指令碼宣告的資料來源不必等連線就辨識得出來(string sql, string name, SqlObjectKind kind)
    {
        var lookup = SqlObjectLookup.Create(sql, sql.LastIndexOf(name, StringComparison.Ordinal))!;

        // 沒有快照就是「還沒連上、或快取還沒載入」，這一支不受它影響。
        var candidate = lookup.FindCandidate(null)!;

        Assert.False(candidate.NeedsColumn);
        Assert.Equal(kind, candidate.Object.Kind);
        Assert.Equal(name, candidate.Object.Name);
        Assert.Equal(new[] { "ID", "Name" }, candidate.ScriptDetail!.Columns.Select(column => column.Name));

        // 明細跟著位置一起交出去；呼叫端不必回頭問中繼資料，問了也只會白跑一次。
        Assert.Same(candidate.ScriptDetail, lookup.Locate(candidate)!.Detail);
    }

    /// <summary>限定字指向指令碼宣告的資料來源時，游標底下的是它的欄位。</summary>
    [Theory]
    [InlineData("CREATE TABLE #Loan (CopyNo INT); SELECT t.CopyNo FROM #Loan t")]
    [InlineData("CREATE TABLE #Loan (CopyNo INT); SELECT #Loan.CopyNo FROM #Loan")]
    public void 指令碼宣告的資料來源也解析得出欄位(string sql)
    {
        var lookup = SqlObjectLookup.Create(sql, sql.LastIndexOf("CopyNo", StringComparison.Ordinal))!;
        var candidate = lookup.FindCandidate(null)!;

        Assert.True(candidate.NeedsColumn);
        Assert.Equal("CopyNo", lookup.Locate(candidate)!.Column!.Name);
    }

    /// <summary>
    /// 別名優先於同名的宣告，與資料庫物件同一條規則。
    /// </summary>
    /// <remarks>
    /// 少了這一條，指令碼別處剛好有一個叫 <c>c</c> 的 CTE，就會讓
    /// <c>FROM dbo.Lib_Reader c</c> 之後的 <c>c</c> 指到那個 CTE 去。
    /// </remarks>
    [Fact]
    public void 別名指向資料庫物件時不被同名的CTE搶走()
    {
        const string sql = ";WITH c AS (SELECT ID FROM dbo.Other) SELECT * FROM dbo.Lib_Reader c";
        var lookup = SqlObjectLookup.Create(sql, sql.Length - 1)!;

        var table = Table(1, "Library");

        Assert.Null(lookup.FindCandidate(null));
        Assert.Same(table, lookup.FindCandidate(Snapshot(table))!.Object);
    }

    /// <summary>
    /// 沒有限定字的欄位也要認得出來。
    /// </summary>
    /// <remarks>
    /// 少了這一條，<c>SELECT ReaderId FROM dbo.Lib_Reader</c> 停在 <c>ReaderId</c> 上
    /// 什麼都沒有，多打一個別名寫成 <c>r.ReaderId</c> 卻答得出來。
    /// </remarks>
    [Theory]
    [InlineData("SELECT ReaderId FROM dbo.Lib_Reader")]
    [InlineData("SELECT * FROM dbo.Lib_Reader WHERE ReaderId = 1")]
    [InlineData("UPDATE dbo.Lib_Reader SET ReaderId = 1")]
    public void 未限定的欄位也解析得出所屬資料表(string sql)
    {
        var table = Table(1, "Library");
        var column = new SqlColumnInfo(1, "ReaderId", "int", false);
        var detail = new SqlObjectDetail(table, new[] { column });
        var lookup = SqlObjectLookup.Create(sql, sql.IndexOf("ReaderId", StringComparison.Ordinal))!;

        var candidate = lookup.FindCandidate(Snapshot(table), _ => detail)!;

        Assert.True(candidate.NeedsColumn);
        Assert.Same(table, candidate.Object);
        Assert.Same(column, lookup.Locate(candidate, detail)!.Column);

        // 明細還沒進快取時不亂猜；呼叫端預載之後同一份語法分析要能重新回答。
        Assert.Null(lookup.FindCandidate(Snapshot(table)));
    }

    /// <summary>指令碼自己宣告的來源不必等連線，未限定的欄位也一樣。</summary>
    [Theory]
    [InlineData("CREATE TABLE #Loan (CopyNo INT); SELECT CopyNo FROM #Loan", "#Loan")]
    [InlineData("DECLARE @rows TABLE (CopyNo INT); SELECT CopyNo FROM @rows", "@rows")]
    [InlineData(";WITH c AS (SELECT CopyNo FROM dbo.Loan) SELECT CopyNo FROM c", "c")]
    public void 未限定的欄位在指令碼宣告的來源上不必等連線(string sql, string name)
    {
        var lookup = SqlObjectLookup.Create(sql, sql.LastIndexOf("CopyNo", StringComparison.Ordinal))!;

        var candidate = lookup.FindCandidate(null)!;

        Assert.True(candidate.NeedsColumn);
        Assert.Equal(name, candidate.Object.Name);
        Assert.Equal("CopyNo", lookup.Locate(candidate)!.Column!.Name);
    }

    /// <summary>
    /// 停在資料來源那一段名稱上時它是物件，不是同名的欄位。
    /// </summary>
    /// <remarks>
    /// 少了這一道，<c>FROM Lib_Reader</c> 的那個名稱會被自己的同名欄位搶走，
    /// 提示畫的是一個欄位，而使用者指的是整張表。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM Lib_Reader")]
    [InlineData("SELECT * FROM Lib_Reader AS Lib_Reader")]
    public void 停在資料來源本身時仍然解析成物件(string sql)
    {
        var table = Table(1, "Library");
        var detail = new SqlObjectDetail(table, new[] { new SqlColumnInfo(1, "Lib_Reader", "int", false) });
        var lookup = SqlObjectLookup.Create(sql, sql.IndexOf("Lib_Reader", StringComparison.Ordinal))!;

        var candidate = lookup.FindCandidate(Snapshot(table), _ => detail)!;

        Assert.False(candidate.NeedsColumn);
        Assert.Same(table, candidate.Object);
    }

    /// <summary>兩個來源都有這個欄位時 T-SQL 自己也判不出來，挑一個等於猜。</summary>
    [Fact]
    public void 兩個來源都有同名欄位時不猜()
    {
        const string sql = "SELECT CopyNo FROM dbo.Lib_Reader r JOIN dbo.Loan l ON r.ReaderId = l.ReaderId";
        var reader = Table(1, "Library");
        var loan = new SqlObjectInfo(2, "dbo", "Loan", SqlObjectKind.Table, "Library");
        var snapshot = new SqlDatabaseSnapshot(
            "Library",
            new[] { reader, loan },
            new[] { "dbo" },
            Array.Empty<string>(),
            DateTimeOffset.UtcNow);
        var column = new SqlColumnInfo(1, "CopyNo", "int", false);
        var lookup = SqlObjectLookup.Create(sql, sql.IndexOf("CopyNo", StringComparison.Ordinal))!;

        Assert.Null(lookup.FindCandidate(snapshot, owner => new SqlObjectDetail(owner, new[] { column })));
    }

    /// <summary>
    /// 保留字裸寫一定不是欄位參考，別為它把來源明細掃過一輪。
    /// </summary>
    /// <remarks>
    /// 滑鼠停在 <c>SELECT</c>、<c>FROM</c> 上的次數遠多於停在欄位上，而那條路徑
    /// 在滑鼠移動的軌跡上。加了方括號就是識別字，那時照樣要回答。
    /// </remarks>
    [Fact]
    public void 保留字不會去問來源明細()
    {
        var table = Table(1, "Library");
        var detail = new SqlObjectDetail(table, new[] { new SqlColumnInfo(1, "Key", "int", false) });

        const string bare = "SELECT Key FROM dbo.Lib_Reader";
        var asked = 0;
        var lookup = SqlObjectLookup.Create(bare, bare.IndexOf("Key", StringComparison.Ordinal))!;

        Assert.Null(lookup.FindCandidate(Snapshot(table), _ => { asked++; return detail; }));
        Assert.Equal(0, asked);

        const string quoted = "SELECT [Key] FROM dbo.Lib_Reader";
        var quotedLookup = SqlObjectLookup.Create(quoted, quoted.IndexOf("Key", StringComparison.Ordinal))!;

        Assert.True(quotedLookup.FindCandidate(Snapshot(table), _ => detail)!.NeedsColumn);
    }

    /// <summary>等得起查詢的呼叫端靠這一份決定要把哪幾份明細載齊。</summary>
    [Fact]
    public void 未限定的欄位交得出要載明細的來源()
    {
        const string sql = "SELECT ReaderId FROM dbo.Lib_Reader";
        var table = Table(1, "Library");
        var lookup = SqlObjectLookup.Create(sql, sql.IndexOf("ReaderId", StringComparison.Ordinal))!;

        Assert.Same(table, Assert.Single(lookup.FindColumnSources(Snapshot(table))));

        // 停在資料來源自己身上時沒有欄位要解析，也就沒有明細要載。
        var onSource = SqlObjectLookup.Create(sql, sql.IndexOf("Lib_Reader", StringComparison.Ordinal))!;
        Assert.Empty(onSource.FindColumnSources(Snapshot(table)));
    }

    private static SqlObjectInfo Table(int id, string database) =>
        new(id, "dbo", "Lib_Reader", SqlObjectKind.Table, database);

    private static SqlDatabaseSnapshot Snapshot(SqlObjectInfo table) =>
        new(table.DatabaseName!, new[] { table }, new[] { "dbo" }, Array.Empty<string>(), DateTimeOffset.UtcNow);
}
