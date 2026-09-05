using System;
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

    private static SqlObjectInfo Table(int id, string database) =>
        new(id, "dbo", "Lib_Reader", SqlObjectKind.Table, database);

    private static SqlDatabaseSnapshot Snapshot(SqlObjectInfo table) =>
        new(table.DatabaseName!, new[] { table }, new[] { "dbo" }, Array.Empty<string>(), DateTimeOffset.UtcNow);
}
