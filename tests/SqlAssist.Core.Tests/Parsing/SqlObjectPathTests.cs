using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

public sealed class SqlObjectPathTests
{
    /// <remarks>
    /// 右對齊是 T-SQL 自己的規則，也是「兩段式不會被誤讀成資料庫加名稱」的唯一依據。
    /// 讀反的症狀是 <c>dbo.Loan</c> 被當成跨資料庫呼叫，整份欄位建議改去查一個
    /// 叫做 dbo 的資料庫。
    /// </remarks>
    [Theory]
    [InlineData(new[] { "Loan" }, null, null, null, "Loan")]
    [InlineData(new[] { "dbo", "Loan" }, null, null, "dbo", "Loan")]
    [InlineData(new[] { "LibArchive", "dbo", "Loan" }, null, "LibArchive", "dbo", "Loan")]
    [InlineData(new[] { "192.0.2.10", "LibArchive", "dbo", "Loan" }, "192.0.2.10", "LibArchive", "dbo", "Loan")]
    public void 完整名稱右對齊(
        string[] parts,
        string? server,
        string? database,
        string? schema,
        string name)
    {
        Assert.True(SqlObjectPath.TryParseName(parts, out var path));
        Assert.NotNull(path);
        Assert.Equal(server, path!.ServerName);
        Assert.Equal(database, path.DatabaseName);
        Assert.Equal(schema, path.SchemaName);
        Assert.Equal(name, path.Name);
        Assert.True(path.HasName);
    }

    /// <remarks>
    /// 限定字的右端是結構描述而不是名稱。對錯一格的症狀是
    /// <c>LibArchive.dbo.</c> 被讀成「結構描述 LibArchive、名稱 dbo」，
    /// 於是清單去查一個不存在的結構描述，一筆都列不出來。
    /// </remarks>
    [Theory]
    [InlineData(new[] { "dbo" }, null, null, "dbo")]
    [InlineData(new[] { "LibArchive", "dbo" }, null, "LibArchive", "dbo")]
    [InlineData(new[] { "192.0.2.10", "LibArchive", "dbo" }, "192.0.2.10", "LibArchive", "dbo")]
    public void 限定字右對齊到結構描述(
        string[] parts,
        string? server,
        string? database,
        string? schema)
    {
        Assert.True(SqlObjectPath.TryParseQualifier(parts, out var path));
        Assert.NotNull(path);
        Assert.Equal(server, path!.ServerName);
        Assert.Equal(database, path.DatabaseName);
        Assert.Equal(schema, path.SchemaName);
        Assert.False(path.HasName);
    }

    /// <remarks>
    /// <c>LibArchive..Loan</c> 的意思是「這個資料庫，結構描述照預設解析」。
    /// 存成空字串的話下游會拿它去比對，而沒有任何結構描述叫做空字串，
    /// 症狀是這個寫法永遠一筆都比不中。
    /// </remarks>
    [Fact]
    public void 空的中間段當成沒寫()
    {
        Assert.True(SqlObjectPath.TryParseName(new[] { "LibArchive", string.Empty, "Loan" }, out var path));
        Assert.NotNull(path);
        Assert.Null(path!.SchemaName);
        Assert.Equal("LibArchive", path.DatabaseName);
        Assert.True(path.IsCrossDatabase);
        Assert.False(path.IsCrossServer);
    }

    /// <remarks>
    /// 連結伺服器加名稱、中間兩段都省略；點號不能跟著省，少一個就是另一個名稱。
    /// </remarks>
    [Fact]
    public void 只有伺服器與名稱時中間的點號要留著()
    {
        Assert.True(SqlObjectPath.TryParseName(
            new[] { "192.0.2.10", string.Empty, string.Empty, "Loan" },
            out var path));

        Assert.NotNull(path);
        Assert.Equal("192.0.2.10...Loan", path!.ToString());
    }

    [Fact]
    public void 限定字保留尾端點號()
    {
        Assert.True(SqlObjectPath.TryParseQualifier(new[] { "LibArchive", "dbo" }, out var path));
        Assert.Equal("LibArchive.dbo.", path!.ToString());
    }

    /// <remarks>
    /// 超過上限不取後四段：那不是寫錯了還救得回來的名稱，猜一個出來只會讓下游
    /// 拿去查一個使用者沒有指名的東西。
    /// </remarks>
    [Fact]
    public void 超過四段不合法()
    {
        Assert.False(SqlObjectPath.TryParseName(
            new[] { "192.0.2.10", "LibArchive", "dbo", "Loan", "CopyNo" },
            out var path));

        Assert.Null(path);
    }

    [Fact]
    public void 限定字超過三段不合法()
    {
        Assert.False(SqlObjectPath.TryParseQualifier(
            new[] { "192.0.2.10", "LibArchive", "dbo", "Loan" },
            out _));
    }

    /// <remarks>
    /// 最後一段是空的代表名稱還沒打完（<c>LibArchive.dbo.</c>），那是限定字。
    /// 當成名稱讀進來的話，下游會拿一個空名稱去查中繼資料。
    /// </remarks>
    [Fact]
    public void 名稱不可為空()
    {
        Assert.False(SqlObjectPath.TryParseName(new[] { "dbo", string.Empty }, out _));
        Assert.False(SqlObjectPath.TryParseName(new string[0], out _));
    }

    [Fact]
    public void 全是空段不算限定字()
    {
        Assert.False(SqlObjectPath.TryParseQualifier(new[] { string.Empty, string.Empty }, out _));
    }

    /// <remarks>
    /// 只比位置、不比名稱：問這個問題的是「這兩筆要不要走同一份中繼資料」。
    /// 大小寫不敏感，因為 SQL Server 的資料庫與伺服器名稱本來就是。
    /// </remarks>
    [Fact]
    public void 同一個來源只看伺服器與資料庫()
    {
        Assert.True(SqlObjectPath.TryParseName(new[] { "LibArchive", "dbo", "Loan" }, out var loan));
        Assert.True(SqlObjectPath.TryParseName(new[] { "libarchive", "dbo", "Branch" }, out var branch));
        Assert.True(SqlObjectPath.TryParseName(new[] { "dbo", "Loan" }, out var local));

        Assert.True(loan!.HasSameSource(branch));
        Assert.False(loan.HasSameSource(local));
        Assert.False(loan.HasSameSource(null));
    }

    [Fact]
    public void 沒有限定的名稱就是目前這條連線()
    {
        Assert.True(SqlObjectPath.TryParseName(new[] { "dbo", "Loan" }, out var path));

        Assert.True(path!.IsLocal);
        Assert.False(path.IsCrossDatabase);
        Assert.False(path.IsCrossServer);
    }
}
