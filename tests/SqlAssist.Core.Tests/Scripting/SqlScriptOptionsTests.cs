using System;
using System.Linq;
using SqlAssist.Core.Scripting;
using Xunit;

namespace SqlAssist.Core.Tests.Scripting;

public sealed class SqlScriptOptionsTests
{
    [Theory]
    [InlineData(SqlScriptStyle.Fidelity)]
    [InlineData(SqlScriptStyle.SsmsNative)]
    [InlineData(SqlScriptStyle.Minimal)]
    public void 每一組風格都回報自己的名稱(SqlScriptStyle style)
    {
        Assert.Equal(style, SqlScriptOptions.ForStyle(style).Style);
    }

    [Fact]
    public void Fidelity風格把條件約束寫成獨立敘述並留型別空格()
    {
        var options = SqlScriptOptions.Fidelity;

        Assert.Equal(SqlConstraintPlacement.SeparateStatement, options.PrimaryKeyPlacement);
        Assert.True(options.SpaceBeforeTypeArguments);
        Assert.True(options.OmitAscendingKeyword);
        Assert.Equal(SqlSetOptionOutput.None, options.SetOptions);
        Assert.Equal(SqlCollationOutput.Always, options.Collation);
    }

    [Fact]
    public void SsmsNative風格把主索引鍵內嵌並依目錄反推SET選項()
    {
        var options = SqlScriptOptions.SsmsNative;

        Assert.Equal(SqlConstraintPlacement.Inline, options.PrimaryKeyPlacement);
        Assert.Equal(SqlSetOptionOutput.FromCatalog, options.SetOptions);
        Assert.False(options.OmitAscendingKeyword);
        Assert.Equal("\t", options.Indent);
    }

    [Fact]
    public void Minimal風格省略系統配的名稱與填色群組()
    {
        var options = SqlScriptOptions.Minimal;

        Assert.Equal(SqlConstraintNaming.OnlyUserNamed, options.ConstraintNaming);
        Assert.False(options.IncludeFilegroup);
        Assert.False(options.QuoteDataTypes);
        Assert.Equal(SqlBatchSeparation.None, options.BatchSeparation);
    }

    [Fact]
    public void 未知的風格擲出例外而不是安靜地退回預設值()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SqlScriptOptions.ForStyle((SqlScriptStyle)999));
    }

    [Fact]
    public void 覆寫一項不會動到其他項()
    {
        var options = SqlScriptOptions.Fidelity with { IncludeExtendedProperties = false };

        Assert.False(options.IncludeExtendedProperties);
        Assert.Equal(SqlScriptOptions.Fidelity.PrimaryKeyPlacement, options.PrimaryKeyPlacement);
        Assert.True(SqlScriptOptions.Fidelity.IncludeExtendedProperties);
    }

    [Fact]
    public void 三組風格彼此不同()
    {
        var styles = Enum.GetValues(typeof(SqlScriptStyle))
            .Cast<SqlScriptStyle>()
            .Select(SqlScriptOptions.ForStyle)
            .ToArray();

        Assert.Equal(styles.Length, styles.Distinct().Count());
    }
}
