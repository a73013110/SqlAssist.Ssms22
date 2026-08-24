using Xunit;

namespace SqlAssist.Metadata.Tests;

public sealed class SqlObjectModelTests
{
    [Theory]
    [InlineData("U", SqlObjectKind.Table)]
    [InlineData("V", SqlObjectKind.View)]
    [InlineData("P", SqlObjectKind.Procedure)]
    [InlineData("PC", SqlObjectKind.Procedure)]
    [InlineData("FN", SqlObjectKind.ScalarFunction)]
    [InlineData("FS", SqlObjectKind.ScalarFunction)]
    [InlineData("IF", SqlObjectKind.InlineTableFunction)]
    [InlineData("TF", SqlObjectKind.TableValuedFunction)]
    [InlineData("FT", SqlObjectKind.TableValuedFunction)]
    [InlineData("SN", SqlObjectKind.Synonym)]
    [InlineData("X", SqlObjectKind.Unknown)]
    [InlineData(null, SqlObjectKind.Unknown)]
    public void 對應sys_objects型別(string? type, SqlObjectKind expected)
    {
        Assert.Equal(expected, SqlObjectKinds.FromSysObjectType(type));
    }

    [Fact]
    public void sys_objects型別含尾端空白仍可對應()
    {
        // sys.objects.type 是 char(2)，非兩字元的型別會補空白。
        Assert.Equal(SqlObjectKind.Table, SqlObjectKinds.FromSysObjectType("U "));
    }

    [Theory]
    [InlineData(SqlObjectKind.Table, true)]
    [InlineData(SqlObjectKind.View, true)]
    [InlineData(SqlObjectKind.Synonym, true)]
    [InlineData(SqlObjectKind.InlineTableFunction, true)]
    [InlineData(SqlObjectKind.Procedure, false)]
    [InlineData(SqlObjectKind.ScalarFunction, false)]
    public void 判斷是否為資料來源(SqlObjectKind kind, bool expected)
    {
        Assert.Equal(expected, kind.IsDataSource());
    }

    [Theory]
    [InlineData(SqlObjectKind.Table, true)]
    [InlineData(SqlObjectKind.View, true)]
    [InlineData(SqlObjectKind.Procedure, false)]
    public void 判斷是否有欄位(SqlObjectKind kind, bool expected)
    {
        Assert.Equal(expected, kind.HasColumns());
    }

    [Theory]
    [InlineData(SqlObjectKind.Procedure, true)]
    [InlineData(SqlObjectKind.View, true)]
    [InlineData(SqlObjectKind.ScalarFunction, true)]
    [InlineData(SqlObjectKind.Table, false)]
    [InlineData(SqlObjectKind.Synonym, false)]
    public void 判斷是否為可取得定義的模組(SqlObjectKind kind, bool expected)
    {
        Assert.Equal(expected, kind.IsModule());
    }

    [Theory]
    [InlineData("Lib_Reader", "[Lib_Reader]")]
    [InlineData("Order Detail", "[Order Detail]")]
    [InlineData("Weird]Name", "[Weird]]Name]")]
    public void 括住識別字並跳脫右方括號(string name, string expected)
    {
        Assert.Equal(expected, SqlIdentifier.Quote(name));
    }

    [Theory]
    [InlineData("Lib_Reader", true)]
    [InlineData("_temp", true)]
    [InlineData("#tmp", false)]
    [InlineData("Order Detail", false)]
    [InlineData("1Table", false)]
    [InlineData("", false)]
    public void 判斷識別字是否需要括號(string name, bool isRegular)
    {
        Assert.Equal(isRegular, SqlIdentifier.IsRegular(name));
        Assert.Equal(isRegular ? name : SqlIdentifier.Quote(name), SqlIdentifier.QuoteIfNeeded(name));
    }

    [Fact]
    public void 資料表預覽列出欄位結構()
    {
        var detail = new SqlObjectDetail(
            new SqlObjectInfo(1, "dbo", "Lib_Reader", SqlObjectKind.Table),
            new[]
            {
                new SqlColumnInfo(1, "UserId", "int", false, isIdentity: true, isPrimaryKey: true),
                new SqlColumnInfo(2, "UserName", "nvarchar(50)", true)
            });

        var preview = detail.BuildPreview();

        Assert.Contains("Table [dbo].[Lib_Reader]", preview);
        Assert.Contains("[UserId] int IDENTITY NOT NULL -- PK,", preview);
        Assert.Contains("[UserName] nvarchar(50) NULL", preview);
    }

    [Fact]
    public void 模組預覽顯示原始定義()
    {
        var detail = new SqlObjectDetail(
            new SqlObjectInfo(2, "dbo", "usp_Test", SqlObjectKind.Procedure),
            definition: "CREATE PROCEDURE dbo.usp_Test AS SELECT 1");

        Assert.Equal("CREATE PROCEDURE dbo.usp_Test AS SELECT 1", detail.BuildPreview());
    }

    [Fact]
    public void 沒有定義的模組退回顯示參數簽章()
    {
        var detail = new SqlObjectDetail(
            new SqlObjectInfo(3, "dbo", "usp_Encrypted", SqlObjectKind.Procedure),
            parameters: new[] { new SqlParameterInfo(1, "@Id", "int", false) });

        var preview = detail.BuildPreview();

        Assert.Contains("Procedure [dbo].[usp_Encrypted]", preview);
        Assert.Contains("@Id int", preview);
    }

    [Fact]
    public void 尚未載入欄位的資料表預覽會標示出來()
    {
        var detail = new SqlObjectDetail(new SqlObjectInfo(4, "dbo", "Empty", SqlObjectKind.Table));

        Assert.Contains("尚未載入欄位", detail.BuildPreview());
    }
}
