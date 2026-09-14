using System;
using System.Security.Cryptography;
using System.Text;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlContentTests
{
    [Theory]
    [InlineData("")]
    [InlineData("SELECT * FROM Lib_Reader;")]
    [InlineData("SELECT N'讀者📚';\r\n")]
    public void HashMatchesUtf16LittleEndian(string sql)
    {
        var content = SqlContent.Create(sql);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(sql))).ToLowerInvariant(), content.ContentHash);
        Assert.Equal("sha256-utf16le:" + content.ContentHash, content.ContentId);
        Assert.Equal(sql, content.SqlText);
        Assert.Equal(sql.Length, content.Length);
        Assert.Equal(content.ContentId, SqlContent.Create(sql).ContentId);
    }

    [Fact]
    public void LargeContentHashesAcrossBufferBoundary()
    {
        var sql = new string('讀', 16000) + "📚";
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(sql))).ToLowerInvariant(), SqlContent.Create(sql).ContentHash);
    }

    [Theory]
    [InlineData("SELECT 1", "select 1")]
    [InlineData("SELECT 1", "SELECT 1 ")]
    [InlineData("SELECT 1\n", "SELECT 1\r\n")]
    public void DoesNotNormalizeDifferentSql(string first, string second) =>
        Assert.NotEqual(SqlContent.Create(first).ContentId, SqlContent.Create(second).ContentId);

    [Fact]
    public void UnpairedSurrogateDoesNotHashAsReplacementCharacter() =>
        Assert.NotEqual(SqlContent.Create(new string((char)0xd800, 1)).ContentId, SqlContent.Create("�").ContentId);
}
