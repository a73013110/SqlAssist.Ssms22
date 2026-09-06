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
        bool primaryKey = false,
        bool generatedAlways = false,
        bool sparse = false,
        bool rowGuidCol = false)
    {
        return new SqlColumnInfo(
            1,
            "Id",
            "int",
            nullable,
            identity,
            computed,
            primaryKey,
            isGeneratedAlways: generatedAlways,
            script: new SqlColumnScriptDetail(
                "int",
                4,
                10,
                0,
                isSparse: sparse,
                isRowGuidCol: rowGuidCol));
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
            Column(
                nullable: false,
                identity: true,
                computed: true,
                primaryKey: true,
                generatedAlways: true,
                sparse: true,
                rowGuidCol: true));

        Assert.Equal(
            new[]
            {
                SqlColumnFlag.PrimaryKey,
                SqlColumnFlag.NotNull,
                SqlColumnFlag.Identity,
                SqlColumnFlag.RowGuidCol,
                SqlColumnFlag.Sparse,
                SqlColumnFlag.GeneratedAlways,
                SqlColumnFlag.Computed
            },
            flags);
    }

    /// <remarks>
    /// <c>SPARSE</c> 與 <c>ROWGUIDCOL</c> 在 <see cref="SqlColumnScriptDetail"/> 裡，
    /// 與資料行同一次查詢回來；沒查到細節時那份是空值，於是自然不標——
    /// 不會變成一句「這一行不是 SPARSE」。
    /// </remarks>
    [Fact]
    public void 沒有查到細節時不標稀疏與資料列識別()
    {
        var column = new SqlColumnInfo(1, "Id", "int", isNullable: true);

        Assert.Empty(SqlColumnPresentation.Flags(column));
    }

    /// <remarks>
    /// 值得標的理由與 IDENTITY 同一條：這一行寫不進去，而看的人多半正要寫一句
    /// <c>INSERT</c>。
    /// </remarks>
    [Fact]
    public void 引擎產生的欄位標出來()
    {
        Assert.Equal(
            new[] { SqlColumnFlag.GeneratedAlways },
            SqlColumnPresentation.Flags(Column(generatedAlways: true)));
    }

    [Theory]
    [InlineData(SqlColumnFlag.PrimaryKey, "PK")]
    [InlineData(SqlColumnFlag.NotNull, "NOT NULL")]
    [InlineData(SqlColumnFlag.Identity, "IDENTITY")]
    [InlineData(SqlColumnFlag.RowGuidCol, "ROWGUIDCOL")]
    [InlineData(SqlColumnFlag.Sparse, "SPARSE")]
    [InlineData(SqlColumnFlag.GeneratedAlways, "GENERATED ALWAYS")]
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
