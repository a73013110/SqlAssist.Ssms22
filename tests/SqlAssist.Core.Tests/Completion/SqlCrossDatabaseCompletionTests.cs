using System.Linq;
using SqlAssist.Core.Completion;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 限定字是一條路徑，不是一個識別字。
/// </summary>
/// <remarks>
/// 只留最右邊一段的症狀沒有徵兆：<c>LibArchive.dbo.</c> 會列出<b>目前連線</b>的
/// dbo 物件，清單看起來完全正常，選中的每一個名稱卻都不是使用者指名的那一個。
/// </remarks>
public sealed class SqlCrossDatabaseCompletionTests
{
    private static SqlCompletionContext Analyze(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        return SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);
    }

    [Fact]
    public void 三段式限定字讀得出資料庫與結構描述()
    {
        var context = Analyze("SELECT * FROM LibArchive.dbo.|");

        Assert.NotNull(context.QualifierPath);
        Assert.Equal("LibArchive", context.QualifierPath!.DatabaseName);
        Assert.Equal("dbo", context.QualifierPath.SchemaName);
        Assert.Equal("dbo", context.Qualifier);
        Assert.False(context.QualifierPath.IsLocal);
    }

    /// <remarks>
    /// 只剝一段的話 beforeQualifier 停在 <c>FROM LibArchive.</c> 上，
    /// 位置判斷連 <c>FROM</c> 都看不到，目標會退成 Any——清單於是混進關鍵字與片段。
    /// </remarks>
    [Fact]
    public void 剝掉整串限定字之後位置判斷仍看得到FROM()
    {
        var context = Analyze("SELECT * FROM LibArchive.dbo.|");

        Assert.Equal(CompletionTarget.DataSource, context.Target);
    }

    [Fact]
    public void 四段式限定字讀得出以位址命名的連結伺服器()
    {
        var context = Analyze("SELECT TOP 100 * FROM [192.0.2.10].[LibArchive].[dbo].|");

        Assert.NotNull(context.QualifierPath);
        Assert.Equal("192.0.2.10", context.QualifierPath!.ServerName);
        Assert.Equal("LibArchive", context.QualifierPath.DatabaseName);
        Assert.Equal("dbo", context.QualifierPath.SchemaName);
        Assert.True(context.QualifierPath.IsCrossServer);
        Assert.Equal(CompletionTarget.DataSource, context.Target);
    }

    /// <remarks>
    /// <c>LibArchive..</c> 有路徑卻沒有結構描述那一段。「有沒有限定字」因此要問
    /// 路徑而不是問 <see cref="SqlCompletionContext.Qualifier"/>——問錯的症狀是
    /// 插入文字自己補上 <c>[dbo].</c>，寫出 <c>LibArchive..[dbo].[Loan]</c>。
    /// </remarks>
    [Fact]
    public void 省略結構描述的限定字仍算限定字()
    {
        var context = Analyze("SELECT * FROM LibArchive..|");

        Assert.NotNull(context.QualifierPath);
        Assert.Equal("LibArchive", context.QualifierPath!.DatabaseName);
        Assert.Null(context.QualifierPath.SchemaName);
        Assert.Null(context.Qualifier);
        Assert.True(context.IsValid);
    }

    [Fact]
    public void 兩段式限定字不受影響()
    {
        var context = Analyze("SELECT * FROM dbo.|");

        Assert.NotNull(context.QualifierPath);
        Assert.True(context.QualifierPath!.IsLocal);
        Assert.Equal("dbo", context.Qualifier);
        Assert.Equal(CompletionTarget.DataSource, context.Target);
    }

    /// <remarks>
    /// 別名只有一段。多段的限定字拿最右邊那一段去比對別名的話，剛好取名叫
    /// <c>dbo</c> 的別名會讓清單改列它的欄位。
    /// </remarks>
    [Fact]
    public void 多段限定字不會被當成別名()
    {
        var context = Analyze("SELECT LibArchive.dbo.| FROM Loan AS dbo");

        Assert.NotEqual(CompletionTarget.Column, context.Target);
        Assert.Null(context.ColumnSources);
    }

    [Fact]
    public void 單段限定字仍解析得出別名()
    {
        var context = Analyze("SELECT l.| FROM dbo.Loan AS l");

        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.NotNull(context.ColumnSources);
    }

    /// <remarks>
    /// 取最右邊三段的話，使用者打錯的一串名稱會安靜地變成一個查得到的東西。
    /// </remarks>
    [Fact]
    public void 限定字段數過多時整個不認()
    {
        var context = Analyze("SELECT * FROM a.b.c.d.|");

        Assert.Null(context.QualifierPath);
        Assert.Null(context.Qualifier);
    }

    /// <remarks>
    /// <c>sys.</c> 與 <c>INFORMATION_SCHEMA.</c> 認的是最右邊那一段，
    /// 所以跨資料庫的系統物件也認得出來。
    /// </remarks>
    [Fact]
    public void 跨資料庫的系統結構描述仍算系統物件位置()
    {
        Assert.True(Analyze("SELECT * FROM LibArchive.sys.|").WantsSystemObjects);
        Assert.False(Analyze("SELECT * FROM LibArchive.dbo.|").WantsSystemObjects);
    }

    /// <remarks>
    /// 資料庫名稱只在 <c>USE</c> 之後才對。省略結構描述的 <c>LibArchive..</c> 沒有
    /// 東西可以比對，整份結構描述過濾會被跳過——不特別擋的話，那個資料庫的
    /// 名稱清單就跟著列出來了。
    /// </remarks>
    [Fact]
    public void 點號之後不列資料庫名稱()
    {
        var candidates = new[]
        {
            new SqlSuggestion("LibArchive", "LibArchive", "Database", "USE LibArchive", SuggestionKind.Database),
            new SqlSuggestion("Loan", "Loan", "Table", "Loan", SuggestionKind.Table, schemaName: "dbo")
        };

        var names = SuggestionMatcher
            .Filter(candidates, Analyze("SELECT * FROM LibArchive..|"))
            .Select(suggestion => suggestion.DisplayText)
            .ToArray();

        Assert.Equal(new[] { "Loan" }, names);
    }

    /// <remarks>
    /// 打完第二個點號正是「換一個資料庫」的意思，清單要跟著重開。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM LibArchive.dbo.")]
    [InlineData("SELECT * FROM LibArchive..")]
    [InlineData("SELECT TOP 100 * FROM [192.0.2.10].[LibArchive].[dbo].")]
    public void 跨資料庫限定字之後要重開清單(string textBeforeCaret)
    {
        Assert.True(SqlCompletionTriggers.ShouldReopen(textBeforeCaret));
    }
}
