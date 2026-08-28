using SqlAssist.Core.Completion;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 限定字後方的欄位建議：<c>u.</c> 要能解析成 <c>u</c> 所指的資料表。
/// </summary>
public sealed class SqlColumnCompletionTests
{
    private static SqlCompletionContext Analyze(string sqlWithCaret)
    {
        var caret = sqlWithCaret.IndexOf('|');
        Assert.True(caret >= 0, "測試輸入必須用 | 標出游標位置。");
        return SqlCompletionContextAnalyzer.Analyze(sqlWithCaret.Remove(caret, 1), caret);
    }

    [Fact]
    public void 別名限定字解析成欄位目標()
    {
        var context = Analyze("SELECT u.| FROM dbo.Lib_Reader u");

        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.NotNull(context.QualifiedTable);
        Assert.Equal("Lib_Reader", context.QualifiedTable!.ObjectName);
        Assert.Equal("dbo", context.QualifiedTable.SchemaName);
    }

    /// <summary>
    /// 這是只看游標前文永遠做不到的事：FROM 子句整個在游標後面。
    /// </summary>
    [Fact]
    public void FROM子句在游標後方仍可解析()
    {
        var before = SqlCompletionContextAnalyzer.Analyze("SELECT u.");

        Assert.NotEqual(CompletionTarget.Column, before.Target);
        Assert.Equal(CompletionTarget.Column, Analyze("SELECT u.| FROM dbo.Lib_Reader u").Target);
    }

    [Fact]
    public void 已輸入前綴時仍是欄位目標()
    {
        var context = Analyze("SELECT u.Nam| FROM dbo.Lib_Reader u");

        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.Equal("Nam", context.Prefix);
        Assert.Equal("Lib_Reader", context.QualifiedTable!.ObjectName);
    }

    [Fact]
    public void 沒有別名時用資料表名稱限定()
    {
        var context = Analyze("SELECT Lib_Reader.| FROM dbo.Lib_Reader");

        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.Equal("Lib_Reader", context.QualifiedTable!.ObjectName);
    }

    [Fact]
    public void JOIN的兩個別名都能解析()
    {
        const string sql = "SELECT o.Id, c.| FROM dbo.Orders o JOIN dbo.Publisher c ON o.PublisherId = c.Id";

        Assert.Equal("Publisher", Analyze(sql)!.QualifiedTable!.ObjectName);
    }

    [Fact]
    public void 方括號別名可解析()
    {
        var context = Analyze("SELECT [u x].| FROM dbo.Lib_Reader AS [u x]");

        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.Equal("Lib_Reader", context.QualifiedTable!.ObjectName);
    }

    /// <summary>
    /// 結構描述限定字不是資料來源，必須維持原本列出該結構描述物件的行為。
    /// </summary>
    [Fact]
    public void 結構描述限定字不會被誤判成欄位()
    {
        var context = Analyze("SELECT * FROM dbo.| ");

        Assert.Equal(CompletionTarget.DataSource, context.Target);
        Assert.Equal("dbo", context.Qualifier);
        Assert.Null(context.QualifiedTable);
    }

    /// <summary>衍生資料表與資料表變數查不到欄位，不能宣稱解析成功。</summary>
    [Theory]
    [InlineData("SELECT d.| FROM (SELECT 1 AS X) d")]
    [InlineData("SELECT r.| FROM @rows r")]
    public void 查不到中繼資料的來源不改成欄位目標(string sqlWithCaret)
    {
        var context = Analyze(sqlWithCaret);

        Assert.NotEqual(CompletionTarget.Column, context.Target);
        Assert.Null(context.QualifiedTable);
    }

    [Fact]
    public void 解析不到的限定字維持結構描述解讀()
    {
        var context = Analyze("SELECT zzz.| FROM dbo.Lib_Reader u");

        Assert.Equal("zzz", context.Qualifier);
        Assert.Null(context.QualifiedTable);
    }

    /// <summary>子查詢內的別名不能洩漏到外層。</summary>
    [Fact]
    public void 外層看不到子查詢的別名()
    {
        var context = Analyze("SELECT i.| FROM (SELECT X FROM dbo.Item i) d");

        Assert.Null(context.QualifiedTable);
    }

    /// <summary>
    /// 實機回報的兩個情形：大寫資料表名稱、以及 JOIN 之後在 ON 條件裡用第二個別名。
    /// </summary>
    [Theory]
    [InlineData("SELECT u.| FROM PUBLISHER u", "PUBLISHER")]
    [InlineData("SELECT u.s| FROM PUBLISHER u", "PUBLISHER")]
    [InlineData(
        "SELECT u.* FROM PUBLISHER u INNER JOIN Cat_BookCopy b ON b.|",
        "Cat_BookCopy")]
    [InlineData(
        "SELECT u.* FROM PUBLISHER u INNER JOIN Cat_BookCopy b ON b.Id = u.|",
        "PUBLISHER")]
    public void 實機情形(string sqlWithCaret, string expectedTable)
    {
        var context = Analyze(sqlWithCaret);

        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.Equal(expectedTable, context.QualifiedTable!.ObjectName);
    }

    [Fact]
    public void 字串與註解內不建議欄位()
    {
        Assert.False(Analyze("SELECT 'u.|' FROM dbo.Lib_Reader u").IsValid);
        Assert.False(Analyze("-- u.|\r\nSELECT * FROM dbo.Lib_Reader u").IsValid);
    }

    /// <summary>
    /// 括號裡的別名一樣解析得出來。
    /// </summary>
    /// <remarks>
    /// 實機回報：<c>SELECT COUNT(a.| FROM dbo.PUBLISHER a</c> 沒有任何建議。
    /// 原因是範圍分析器把每一個左括號都當成子查詢，括號裡看不到外層的 FROM 子句，
    /// 別名解析不出來就退回結構描述解讀，而沒有一個物件屬於名為 <c>a</c> 的
    /// 結構描述——清單於是完全是空的。
    /// </remarks>
    [Theory]
    [InlineData("SELECT COUNT(u.|) FROM dbo.Lib_Reader u")]
    [InlineData("SELECT COUNT(u.| FROM dbo.Lib_Reader u")]
    [InlineData("SELECT SUM(u.Amount), MAX(u.|) FROM dbo.Lib_Reader u")]
    [InlineData("SELECT COUNT(DISTINCT u.|) FROM dbo.Lib_Reader u")]
    [InlineData("SELECT ISNULL(u.|, 0) FROM dbo.Lib_Reader u")]
    [InlineData("SELECT ISNULL(SUM(CONVERT(int, u.|)), 0) FROM dbo.Lib_Reader u")]
    [InlineData("SELECT * FROM dbo.Lib_Reader u WHERE (u.| = 1)")]
    [InlineData("SELECT * FROM dbo.Lib_Reader u WHERE Id IN (u.|)")]
    [InlineData("SELECT * FROM dbo.Lib_Reader u ORDER BY DATEPART(day, u.|)")]
    public void 括號內仍解析得出別名(string sqlWithCaret)
    {
        var context = Analyze(sqlWithCaret);

        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.Equal("Lib_Reader", context.QualifiedTable!.ObjectName);
    }

    /// <summary>
    /// 子查詢仍然自成範圍。
    /// </summary>
    /// <remarks>
    /// 規則的另一半：分不出「開啟查詢的括號」與「運算式的括號」的話，
    /// 修好彙總函式就會弄壞子查詢，內層的別名會解析到外層的資料表。
    /// </remarks>
    [Fact]
    public void 子查詢內的別名仍指向子查詢的資料表()
    {
        var context = Analyze(
            "SELECT * FROM dbo.Lib_Reader u WHERE Id IN (SELECT c.| FROM dbo.Lib_Shelf c)");

        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.Equal("Lib_Shelf", context.QualifiedTable!.ObjectName);
    }
}
