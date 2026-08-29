using System;
using System.Linq;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Formatting;

/// <summary>
/// 欄位性質的判斷與顯示文字。
/// </summary>
/// <remarks>
/// 滑鼠提示與結構表格都照這一份標欄位。判斷或名稱改掉時，
/// 兩個表面會一起變——這裡固定的就是「一起變」這件事。
/// </remarks>
public sealed class SqlColumnPresentationTests
{
    private static SqlColumnInfo Column(
        bool nullable = true,
        bool identity = false,
        bool computed = false,
        bool primaryKey = false)
    {
        return new SqlColumnInfo(
            1,
            "Id",
            "int",
            nullable,
            identity,
            computed,
            primaryKey);
    }

    /// <summary>可為 NULL 是 SQL 的預設，沒有徽章就是可為 NULL。</summary>
    [Fact]
    public void 一般欄位沒有任何性質()
    {
        Assert.Empty(SqlColumnPresentation.Flags(Column()));
    }

    /// <summary>順序固定：先講身分（PK），再講限制。</summary>
    [Fact]
    public void 性質依固定順序回報()
    {
        var flags = SqlColumnPresentation.Flags(
            Column(nullable: false, identity: true, computed: true, primaryKey: true));

        Assert.Equal(
            new[]
            {
                SqlColumnFlag.PrimaryKey,
                SqlColumnFlag.NotNull,
                SqlColumnFlag.Identity,
                SqlColumnFlag.Computed
            },
            flags);
    }

    [Theory]
    [InlineData(SqlColumnFlag.PrimaryKey, "PK")]
    [InlineData(SqlColumnFlag.NotNull, "NOT NULL")]
    [InlineData(SqlColumnFlag.Identity, "IDENTITY")]
    [InlineData(SqlColumnFlag.Computed, "COMPUTED")]
    public void 顯示文字用T_SQL自己的說法(SqlColumnFlag flag, string expected)
    {
        Assert.Equal(expected, flag.ToDisplayName());
    }

    /// <summary>新增一種性質卻忘了給名稱，會是空白徽章而不是錯誤，因此明確擋下來。</summary>
    [Fact]
    public void 每一種性質都有顯示文字()
    {
        foreach (SqlColumnFlag flag in Enum.GetValues(typeof(SqlColumnFlag)).Cast<SqlColumnFlag>())
        {
            Assert.False(string.IsNullOrWhiteSpace(flag.ToDisplayName()));
        }
    }
}
