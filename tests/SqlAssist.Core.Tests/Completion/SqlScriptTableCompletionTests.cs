using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 暫存資料表與資料表變數的欄位建議與整句展開。
/// </summary>
/// <remarks>
/// 這兩種名稱中繼資料一列都查不到，過去的結果是它們在建議清單裡看得到名字，
/// 卻在別的地方全面失效：<c>SET |</c> 與 <c>WHERE |</c> 一個欄位都沒有、
/// <c>限定字.</c> 之後是空的、提交 <c>INSERT INTO</c> 只補一個名稱。
/// 欄位就寫在宣告的括號裡，讀出來之後四個位置一起活過來。
/// </remarks>
public sealed class SqlScriptTableCompletionTests
{
    private const string TemporaryTable =
        "CREATE TABLE #Loan\r\n" +
        "(\r\n" +
        "    Id       INT IDENTITY(1,1) PRIMARY KEY,\r\n" +
        "    CopyNo   NVARCHAR(20) NOT NULL,\r\n" +
        "    ReaderId INT NULL\r\n" +
        ");\r\n";

    private const string TableVariable =
        "DECLARE @Loan TABLE\r\n" +
        "(\r\n" +
        "    Id       INT IDENTITY(1,1) PRIMARY KEY,\r\n" +
        "    CopyNo   NVARCHAR(20) NOT NULL,\r\n" +
        "    ReaderId INT NULL\r\n" +
        ");\r\n";

    private static SqlCompletionContext Analyze(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        return SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);
    }

    /// <summary>敘述在游標處看得到的欄位；解析不出來的來源寫成「表 名稱」。</summary>
    private static string[] ScopeColumns(string sqlWithCaret)
    {
        return Analyze(sqlWithCaret).ScopeSources
            .SelectMany(source => source.Kind == SqlColumnSourceKind.Table
                ? new[] { $"表 {source.Table!.ObjectName}" }
                : source.Names)
            .ToArray();
    }

    private static string[] QualifiedColumns(string sqlWithCaret)
    {
        var context = Analyze(sqlWithCaret);

        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.NotNull(context.ColumnSources);

        return context.ColumnSources!.SelectMany(source => source.Names).ToArray();
    }

    /// <summary>
    /// <c>UPDATE … SET</c> 與 <c>WHERE</c> 都列得出欄位。
    /// </summary>
    /// <remarks>
    /// 這正是使用者回報的位置：改一張自己上面幾行才建立的暫存資料表，
    /// 每一個欄位名稱都得自己重打。
    /// </remarks>
    [Theory]
    [InlineData(TemporaryTable + "UPDATE #Loan SET C|")]
    [InlineData(TemporaryTable + "UPDATE #Loan SET CopyNo = 'C1' WHERE R|")]
    [InlineData(TemporaryTable + "SELECT C| FROM #Loan")]
    [InlineData(TemporaryTable + "DELETE FROM #Loan WHERE R|")]
    [InlineData(TableVariable + "UPDATE @Loan SET C|")]
    [InlineData(TableVariable + "UPDATE @Loan SET CopyNo = 'C1' WHERE R|")]
    [InlineData(TableVariable + "SELECT C| FROM @Loan")]
    public void 沒有限定字的位置列得出欄位(string sqlWithCaret)
    {
        Assert.Equal(new[] { "Id", "CopyNo", "ReaderId" }, ScopeColumns(sqlWithCaret));
    }

    /// <summary>方括號寫法指的是同一張表。</summary>
    /// <remarks>
    /// <c>[#Loan]</c> 的詞元值就是 <c>#Loan</c>，兩種寫法解析出同一個名稱。
    /// </remarks>
    [Fact]
    public void 方括號寫法一樣列得出欄位()
    {
        Assert.Equal(
            new[] { "Id", "CopyNo", "ReaderId" },
            ScopeColumns(TemporaryTable + "UPDATE [#Loan] SET C|"));
    }

    [Theory]
    [InlineData(TemporaryTable + "SELECT #Loan.| FROM #Loan")]
    [InlineData(TemporaryTable + "SELECT l.| FROM #Loan l")]
    [InlineData(TableVariable + "SELECT @Loan.| FROM @Loan")]
    [InlineData(TableVariable + "SELECT l.| FROM @Loan l")]
    public void 限定字之後列得出欄位(string sqlWithCaret)
    {
        Assert.Equal(new[] { "Id", "CopyNo", "ReaderId" }, QualifiedColumns(sqlWithCaret));
    }

    /// <summary>
    /// 讀不出資料行的暫存資料表維持原本的行為。
    /// </summary>
    /// <remarks>
    /// <c>SELECT … INTO #tmp</c> 沒有資料行定義。回報一份空清單會讓呼叫端以為
    /// 那張表真的一欄都沒有，而它該做的是照舊去問中繼資料。
    /// </remarks>
    [Fact]
    public void 沒有宣告資料行時仍當成資料表()
    {
        Assert.Equal(
            new[] { "表 #Loan" },
            ScopeColumns("SELECT * INTO #Loan FROM dbo.Loan;\r\nUPDATE #Loan SET C|"));
    }

    /// <summary>
    /// <c>INSERT INTO #tmp</c> 提交之後要展開成整句。
    /// </summary>
    /// <remarks>
    /// 展開需要兩件事：語句的關鍵字起點（要換掉哪一段）與掛在建議項上的資料行清單
    /// （換成什麼）。少了後者的症狀就是使用者說的「按 Tab 只補了名稱，
    /// 不會自動帶出所有欄位及 value」。
    /// </remarks>
    [Theory]
    [InlineData(TemporaryTable + "INSERT INTO #L|", "#Loan", CompletionIntent.InsertStatement)]
    [InlineData(TemporaryTable + "MERGE INTO #L|", "#Loan", CompletionIntent.MergeStatement)]
    public void 暫存資料表帶得出展開整句所需的資料(
        string sqlWithCaret,
        string name,
        CompletionIntent intent)
    {
        var context = Analyze(sqlWithCaret);

        Assert.Equal(intent, context.Intent);
        Assert.True(context.TargetKeywordStart >= 0);

        var suggestion = Assert.Single(
            context.ScriptSources,
            item => item.DisplayText == name);

        Assert.Equal(
            new[] { "Id", "CopyNo", "ReaderId" },
            Assert.IsType<SqlScriptTable>(suggestion.Tag).ColumnNames);
    }

    /// <summary>
    /// 資料表變數走的是變數那條路，一樣要展開成整句。
    /// </summary>
    /// <remarks>
    /// 目標仍然是 <see cref="CompletionTarget.Variable"/>——清單裡放的是他自己宣告的
    /// 名稱——但那句話還沒寫完，與 <c>INSERT INTO dbo.Loan</c> 完全同格。
    /// </remarks>
    [Theory]
    [InlineData(TableVariable + "INSERT INTO @L|", CompletionIntent.InsertStatement)]
    [InlineData(TableVariable + "MERGE INTO @L|", CompletionIntent.MergeStatement)]
    public void 資料表變數帶得出展開整句所需的資料(string sqlWithCaret, CompletionIntent intent)
    {
        var context = Analyze(sqlWithCaret);

        Assert.Equal(CompletionTarget.Variable, context.Target);
        Assert.Equal(intent, context.Intent);
        Assert.True(context.TargetKeywordStart >= 0);

        var suggestion = Assert.Single(
            context.ScriptSources,
            item => item.DisplayText == "@Loan");

        Assert.Equal(
            new[] { "Id", "CopyNo", "ReaderId" },
            Assert.IsType<SqlScriptTable>(suggestion.Tag).ColumnNames);
    }

    /// <summary>
    /// 引數位置的小老鼠不是那句話的目標。
    /// </summary>
    /// <remarks>
    /// <c>EXEC dbo.usp_Renew @|</c> 帶著 <c>ExecuteCall</c> 出去的話，提交會去展開
    /// 一個變數；而 <c>SET @|</c>、<c>WHERE x = @|</c> 根本不是語句的開頭。
    /// </remarks>
    [Theory]
    [InlineData("DECLARE @readerId INT;\r\nEXEC dbo.usp_Renew @|")]
    [InlineData("DECLARE @readerId INT;\r\nSET @|")]
    [InlineData("DECLARE @readerId INT;\r\nSELECT * FROM dbo.Loan WHERE ReaderId = @|")]
    public void 引數位置的變數不展開整句(string sqlWithCaret)
    {
        var context = Analyze(sqlWithCaret);

        Assert.Equal(CompletionTarget.Variable, context.Target);
        Assert.Equal(CompletionIntent.Reference, context.Intent);
        Assert.Equal(-1, context.TargetKeywordStart);
    }
}
