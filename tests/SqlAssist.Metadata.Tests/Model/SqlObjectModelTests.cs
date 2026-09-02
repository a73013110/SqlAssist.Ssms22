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

    /// <remarks>
    /// 資料表值函式的資料行在 <c>sys.columns</c> 裡與資料表的欄位放在一起。
    /// 漏掉它的症狀是 <c>FROM dbo.fn_LoansByReader(0) f</c> 之後 <c>f.</c>
    /// 一個欄位都列不出來，<c>SELECT *</c> 也展不開——第二層根本沒有為它查過。
    /// </remarks>
    [Theory]
    [InlineData(SqlObjectKind.Table, true)]
    [InlineData(SqlObjectKind.View, true)]
    [InlineData(SqlObjectKind.TableType, true)]
    [InlineData(SqlObjectKind.InlineTableFunction, true)]
    [InlineData(SqlObjectKind.TableValuedFunction, true)]
    [InlineData(SqlObjectKind.ScalarFunction, false)]
    [InlineData(SqlObjectKind.Procedure, false)]
    [InlineData(SqlObjectKind.Synonym, false)]
    public void 判斷sys_columns查不查得到資料行(SqlObjectKind kind, bool expected)
    {
        Assert.Equal(expected, kind.HasCatalogColumns());
    }

    /// <remarks>
    /// 資料表值函式查得到資料行，但那是它<b>回傳值</b>的形狀，物件本身是一段
    /// 要填引數才叫得動的程式；滑鼠停留提示與第四層查詢問的是這一條。
    /// </remarks>
    [Theory]
    [InlineData(SqlObjectKind.Table, true)]
    [InlineData(SqlObjectKind.View, true)]
    [InlineData(SqlObjectKind.TableType, true)]
    [InlineData(SqlObjectKind.InlineTableFunction, false)]
    [InlineData(SqlObjectKind.TableValuedFunction, false)]
    [InlineData(SqlObjectKind.Procedure, false)]
    public void 判斷物件本身是不是一組資料行(SqlObjectKind kind, bool expected)
    {
        Assert.Equal(expected, kind.IsTableShaped());
    }

    /// <remarks>
    /// <c>INSERT INTO dbo.fn_LoansByReader</c> 剖析不過，因此提交後的欄位骨架
    /// 展開不能問「查不查得到資料行」；資料表型別在那個位置也不是合法的名稱。
    /// </remarks>
    [Theory]
    [InlineData(SqlObjectKind.Table, true)]
    [InlineData(SqlObjectKind.View, true)]
    [InlineData(SqlObjectKind.TableType, false)]
    [InlineData(SqlObjectKind.InlineTableFunction, false)]
    [InlineData(SqlObjectKind.TableValuedFunction, false)]
    public void 判斷插不插得進去(SqlObjectKind kind, bool expected)
    {
        Assert.Equal(expected, kind.IsInsertTarget());
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

    /// <remarks>
    /// 括號在 T-SQL 裡不是選擇性的：<c>SELECT dbo.fn_DueDate</c> 不是「呼叫但沒
    /// 傳引數」，而是一個語法錯誤。三種函式都算，資料表值的那兩種也一樣。
    /// </remarks>
    [Theory]
    [InlineData(SqlObjectKind.ScalarFunction, true)]
    [InlineData(SqlObjectKind.InlineTableFunction, true)]
    [InlineData(SqlObjectKind.TableValuedFunction, true)]
    [InlineData(SqlObjectKind.Procedure, false)]
    [InlineData(SqlObjectKind.Table, false)]
    public void 判斷是否為要寫括號的函式(SqlObjectKind kind, bool expected)
    {
        Assert.Equal(expected, kind.IsFunction());
    }

    /// <remarks>
    /// 同義字與序列的定義不在 <c>sys.sql_modules</c> 裡，而是目錄檢視上的
    /// 那幾個欄位；組回 T-SQL 的那一份在 <c>SqlCatalogScript</c>。
    /// </remarks>
    [Theory]
    [InlineData(SqlObjectKind.Synonym, true)]
    [InlineData(SqlObjectKind.Sequence, true)]
    [InlineData(SqlObjectKind.Procedure, false)]
    [InlineData(SqlObjectKind.Table, false)]
    public void 判斷定義是否由目錄檢視組出來(SqlObjectKind kind, bool expected)
    {
        Assert.Equal(expected, kind.HasSynthesizedDefinition());
    }

    /// <remarks>
    /// 只剩認不出來的種類寫不出可執行的指令碼。這一條由浮動預覽的指令碼分頁
    /// 與 F12 共用，兩邊各留一份判斷的症狀是同一個物件在兩邊得到不同的東西。
    /// </remarks>
    [Theory]
    [InlineData(SqlObjectKind.Procedure, true)]
    [InlineData(SqlObjectKind.Table, true)]
    [InlineData(SqlObjectKind.TableType, true)]
    [InlineData(SqlObjectKind.Synonym, true)]
    [InlineData(SqlObjectKind.Sequence, true)]
    [InlineData(SqlObjectKind.Unknown, false)]
    public void 判斷這一類寫不寫得出可執行的指令碼(SqlObjectKind kind, bool expected)
    {
        Assert.Equal(expected, kind.HasExecutableScript());
    }

    [Theory]
    [InlineData("Lib_Reader", "[Lib_Reader]")]
    [InlineData("Loan Detail", "[Loan Detail]")]
    [InlineData("Weird]Name", "[Weird]]Name]")]
    public void 括住識別字並跳脫右方括號(string name, string expected)
    {
        Assert.Equal(expected, SqlIdentifier.Quote(name));
    }

    [Theory]
    [InlineData("Lib_Reader", true)]
    [InlineData("_temp", true)]
    [InlineData("Loan Detail", false)]
    [InlineData("1Table", false)]
    [InlineData("", false)]
    public void 判斷識別字的字元形狀(string name, bool isRegular)
    {
        Assert.Equal(isRegular, SqlIdentifier.IsRegular(name));

        // 這幾個都不是保留字，所以形狀合格就等於不必加括號。
        Assert.Equal(isRegular ? name : SqlIdentifier.Quote(name), SqlIdentifier.QuoteIfNeeded(name));
    }

    /// <summary>
    /// 井號與小老鼠開頭的名稱形狀合格，不必加括號。
    /// </summary>
    /// <remarks>
    /// T-SQL 允許的四種開頭裡有這兩種，它們不是例外。判成不合格的症狀是暫存資料表
    /// 被寫成 <c>[#tmp]</c>——合法卻沒有人這樣手寫——而資料表變數被寫成
    /// <c>[@rows]</c>，那根本不是合法的 T-SQL，貼進編輯器就是語法錯誤。
    /// </remarks>
    [Theory]
    [InlineData("#tmp")]
    [InlineData("##Shared")]
    [InlineData("@rows")]
    public void 指令碼宣告的名稱不加括號(string name)
    {
        Assert.True(SqlIdentifier.IsRegular(name));
        Assert.True(SqlIdentifier.IsScriptScoped(name));
        Assert.Equal(name, SqlIdentifier.QuoteIfNeeded(name));
    }

    [Theory]
    [InlineData("Lib_Reader")]
    [InlineData("_temp")]
    [InlineData("Loan Detail")]
    public void 一般名稱不算指令碼宣告(string name)
    {
        Assert.False(SqlIdentifier.IsScriptScoped(name));
    }

    /// <summary>
    /// 沒有結構描述的物件只寫名稱本身。
    /// </summary>
    /// <remarks>
    /// 暫存資料表與資料表變數沒有結構描述，補一個 <c>dbo</c> 是說謊——
    /// 而紀錄檔裡的 <c>[dbo].[#tmp]</c> 會讓人去追一個不存在的物件。
    /// </remarks>
    [Fact]
    public void 沒有結構描述時只寫名稱()
    {
        Assert.Equal(
            "#StationStock",
            new SqlObjectInfo(0, string.Empty, "#StationStock", SqlObjectKind.Table).QualifiedName);
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

    /// <remarks>
    /// 資料表值函式的資料行載入之後，預覽仍然要給定義本文：那份文字同時說得出
    /// 它吃什麼引數、回傳什麼，而一串資料行說不出該怎麼呼叫它。
    /// </remarks>
    [Fact]
    public void 資料表值函式預覽顯示定義而不是回傳的資料行()
    {
        var detail = new SqlObjectDetail(
            new SqlObjectInfo(5, "dbo", "fn_LoansByReader", SqlObjectKind.InlineTableFunction),
            new[] { new SqlColumnInfo(1, "CopyNo", "int", false) },
            new[] { new SqlParameterInfo(1, "@ReaderId", "int", false) },
            "CREATE FUNCTION dbo.fn_LoansByReader (@ReaderId int) RETURNS TABLE AS RETURN SELECT 1 AS CopyNo");

        Assert.Equal(
            "CREATE FUNCTION dbo.fn_LoansByReader (@ReaderId int) RETURNS TABLE AS RETURN SELECT 1 AS CopyNo",
            detail.BuildPreview());
    }

    /// <remarks>
    /// 加密的資料表值函式沒有定義可以給，這時查得到的資料行勝過一行光禿禿的標題。
    /// </remarks>
    [Fact]
    public void 沒有定義時退回顯示查得到的資料行()
    {
        var detail = new SqlObjectDetail(
            new SqlObjectInfo(6, "dbo", "fn_LoansByReader", SqlObjectKind.InlineTableFunction),
            new[] { new SqlColumnInfo(1, "CopyNo", "int", false) });

        Assert.Contains("[CopyNo] int NOT NULL", detail.BuildPreview());
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
