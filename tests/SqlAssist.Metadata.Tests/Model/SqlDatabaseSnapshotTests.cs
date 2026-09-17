using System;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Model;

/// <summary>
/// 列給使用者挑的結構描述與拿來認名稱的結構描述是兩份。
/// </summary>
/// <remarks>
/// 每個資料庫都帶著一批與角色同名的空結構描述，列進清單只會佔掉前幾名；
/// 但認限定字時少了它們，與資料庫同名的那一個會被改認成資料庫。
/// </remarks>
public sealed class SqlDatabaseSnapshotTests
{
    private static readonly SqlDatabaseSnapshot Snapshot = new(
        "Lib",
        new[]
        {
            new SqlObjectInfo(1, "dbo", "Loan", SqlObjectKind.Table),
            new SqlObjectInfo(2, "CAT", "Cat_BookCopy", SqlObjectKind.Table),
            new SqlObjectInfo(3, "dbo", "Lib_Reader", SqlObjectKind.Table)
        },
        new[] { "Cat", "db_denydatareader", "dbo", "guest", "LibArchive" },
        new[] { "LibArchive" },
        DateTimeOffset.UtcNow);

    /// <remarks>順序照完整名單，比對不分大小寫（物件帶的是 <c>CAT</c>）。</remarks>
    [Fact]
    public void 建議只列擁有物件的結構描述()
    {
        Assert.Equal(new[] { "Cat", "dbo" }, Snapshot.SchemasWithObjects);
    }

    [Fact]
    public void 同一份快照只算一次()
    {
        Assert.Same(Snapshot.SchemasWithObjects, Snapshot.SchemasWithObjects);
    }

    [Fact]
    public void 完整名單不受影響()
    {
        Assert.Equal(5, Snapshot.Schemas.Count);
    }

    /// <remarks>
    /// 空結構描述與資料庫同名時仍要認成結構描述：比對順序是「越近的越可信」，
    /// 而這個判斷讀的是完整名單。
    /// </remarks>
    [Fact]
    public void 空結構描述仍然認得出來()
    {
        Assert.True(SqlObjectPath.TryParseQualifier(new[] { "LibArchive" }, out var qualifier));

        var path = SqlQualifierResolver.Resolve(qualifier!, Snapshot);

        Assert.True(path.IsLocal);
        Assert.Equal("LibArchive", path.SchemaName);
    }
}
