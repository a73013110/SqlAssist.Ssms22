using System.Linq;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Model;

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
    public void 判斷識別字的字元形狀(string name, bool isRegular)
    {
        Assert.Equal(isRegular, SqlIdentifier.IsRegular(name));

        // 這幾個都不是保留字，所以形狀合格就等於不必加括號。
        Assert.Equal(isRegular ? name : SqlIdentifier.Quote(name), SqlIdentifier.QuoteIfNeeded(name));
    }

    [Theory]
    [InlineData("Order")]
    [InlineData("Key")]
    [InlineData("User")]
    [InlineData("Group")]
    [InlineData("Select")]
    [InlineData("IDENTITYCOL")]
    public void 保留字即使形狀合格也要加括號(string name)
    {
        // 形狀完全正常，正是最容易漏掉的地方——少了括號，
        // SELECT Order, Name FROM t 插進編輯器就是語法錯誤。
        Assert.True(SqlIdentifier.IsRegular(name));
        Assert.Equal("[" + name + "]", SqlIdentifier.QuoteIfNeeded(name));
    }

    [Theory]
    [InlineData("Output")]
    [InlineData("Rows")]
    [InlineData("Partition")]
    [InlineData("Apply")]
    [InlineData("Next")]
    public void 非保留字的關鍵字不加括號(string name)
    {
        // 這些字在文法上是關鍵字，但當名字寫完全合法。多包一層括號
        // 等於無視使用者關掉「一律加方括號」的用意。
        Assert.Equal(name, SqlIdentifier.QuoteIfNeeded(name));
    }

    private static SqlDatabaseSnapshot Snapshot(params SqlObjectInfo[] objects)
    {
        return new SqlDatabaseSnapshot(
            "Sales",
            objects,
            new[] { "dbo" },
            new[] { "Sales", "master" },
            System.DateTimeOffset.UtcNow);
    }

    [Fact]
    public void 依名稱尋找物件時忽略大小寫()
    {
        var snapshot = Snapshot(new SqlObjectInfo(1, "dbo", "Lib_Reader", SqlObjectKind.Table));

        Assert.Single(snapshot.Find("lib_reader"));
    }

    [Fact]
    public void 未指定結構描述時dbo排在前面()
    {
        var snapshot = Snapshot(
            new SqlObjectInfo(1, "sales", "Publisher", SqlObjectKind.Table),
            new SqlObjectInfo(2, "dbo", "Publisher", SqlObjectKind.Table));

        var matches = snapshot.Find("Publisher");

        Assert.Equal(2, matches.Count);
        Assert.Equal("dbo", matches[0].SchemaName);
    }

    [Fact]
    public void 指定結構描述時只回傳該結構描述的物件()
    {
        var snapshot = Snapshot(
            new SqlObjectInfo(1, "sales", "Publisher", SqlObjectKind.Table),
            new SqlObjectInfo(2, "dbo", "Publisher", SqlObjectKind.Table));

        var matches = snapshot.Find("Publisher", "sales");

        Assert.Single(matches);
        Assert.Equal("sales", matches[0].SchemaName);
    }

    [Fact]
    public void 找不到時回傳空清單()
    {
        var snapshot = Snapshot(new SqlObjectInfo(1, "dbo", "Lib_Reader", SqlObjectKind.Table));

        Assert.Empty(snapshot.Find("Missing"));
        Assert.Empty(snapshot.Find("Lib_Reader", "other"));
        Assert.Empty(snapshot.Find(""));
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

    /// <summary>
    /// 物件清單要照名稱排好。
    /// </summary>
    /// <remarks>
    /// 查詢沒有 ORDER BY，伺服器回傳的大致是建立順序。建議清單同分時保留
    /// 候選項的原始順序，所以這份「原始順序」必須先是有意義的。
    /// </remarks>
    [Fact]
    public void 物件清單依名稱排序()
    {
        var snapshot = Snapshot(
            new SqlObjectInfo(3, "dbo", "Zulu", SqlObjectKind.Table),
            new SqlObjectInfo(1, "sales", "Alpha", SqlObjectKind.View),
            new SqlObjectInfo(2, "dbo", "Alpha", SqlObjectKind.Table));

        Assert.Equal(
            new[] { "dbo.Alpha", "sales.Alpha", "dbo.Zulu" },
            snapshot.Objects.Select(info => $"{info.SchemaName}.{info.Name}").ToArray());
    }
}
