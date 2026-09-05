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

    private static SqlObjectInfo Table(int id, string database) =>
        new(id, "dbo", "Lib_Reader", SqlObjectKind.Table, database);

    private static SqlDatabaseSnapshot Snapshot(SqlObjectInfo table) =>
        new(table.DatabaseName!, new[] { table }, new[] { "dbo" }, Array.Empty<string>(), DateTimeOffset.UtcNow);
}
