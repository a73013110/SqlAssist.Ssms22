using SqlAssist.Core;
using Xunit;

namespace SqlAssist.Core.Tests;

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

    [Fact]
    public void 字串與註解內不建議欄位()
    {
        Assert.False(Analyze("SELECT 'u.|' FROM dbo.Lib_Reader u").IsValid);
        Assert.False(Analyze("-- u.|\r\nSELECT * FROM dbo.Lib_Reader u").IsValid);
    }
}
