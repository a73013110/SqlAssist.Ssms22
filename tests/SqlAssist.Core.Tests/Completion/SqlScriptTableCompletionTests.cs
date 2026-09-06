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
    /// <c>SELECT … INTO #tmp</c> 的資料行寫在選取清單裡。
    /// </summary>
    /// <remarks>
    /// 這種寫法沒有資料行定義，帶型別的那份名冊一個都不會收——但欄位仍然寫在
    /// 使用者眼前，讀得出來，與 CTE 是同一條推理。少了這一條的症狀是使用者上一句
    /// 才建立的暫存資料表，下一句改它時每個欄位都得自己重打。
    /// </remarks>
    [Theory]
    [InlineData("SELECT CopyNo, ReaderId INTO #Loan FROM dbo.Loan;\r\nUPDATE #Loan SET C|")]
    [InlineData("SELECT CopyNo, ReaderId INTO #Loan FROM dbo.Loan;\r\nSELECT C| FROM #Loan")]
    public void SELECT_INTO的資料行讀得出來(string sqlWithCaret)
    {
        Assert.Equal(new[] { "CopyNo", "ReaderId" }, ScopeColumns(sqlWithCaret));
    }

    [Theory]
    [InlineData("SELECT CopyNo, ReaderId INTO #Loan FROM dbo.Loan;\r\nSELECT #Loan.| FROM #Loan")]
    [InlineData("SELECT CopyNo, ReaderId INTO #Loan FROM dbo.Loan;\r\nSELECT l.| FROM #Loan l")]
    public void SELECT_INTO的限定字之後也列得出欄位(string sqlWithCaret)
    {
        Assert.Equal(new[] { "CopyNo", "ReaderId" }, QualifiedColumns(sqlWithCaret));
    }

    /// <summary>
    /// 欄位的出處寫的是那張暫存資料表的名字。
    /// </summary>
    /// <remarks>
    /// 說明欄留空會退回「查詢結果」，而在 <c>UPDATE #Loan SET |</c> 看到那四個字
    /// 會讓人以為認錯了東西。
    /// </remarks>
    [Fact]
    public void SELECT_INTO的欄位說得出出處()
    {
        var context = Analyze("SELECT CopyNo INTO #Loan FROM dbo.Loan;\r\nUPDATE #Loan SET C|");

        Assert.Equal("#Loan", Assert.Single(context.ScopeSources).SourceName);
    }

    /// <summary>
    /// 選取清單是 <c>*</c> 時，攤平到它讀的那張資料表。
    /// </summary>
    /// <remarks>
    /// 那份名單只有中繼資料知道，而這裡本來就會去問——與子查詢的 <c>*</c> 走同一條
    /// 遞迴。攤不平的是<b>投影</b>那一支（滑鼠停留與預覽不等查詢），不是這一支。
    /// </remarks>
    [Fact]
    public void 星號的SELECT_INTO攤平到來源資料表()
    {
        Assert.Equal(
            new[] { "表 Loan" },
            ScopeColumns("SELECT * INTO #Loan FROM dbo.Loan;\r\nUPDATE #Loan SET C|"));
    }

    /// <summary>
    /// 建立它的那一句自己不受影響。
    /// </summary>
    /// <remarks>
    /// <c>INTO</c> 後面那張表是<b>正要建立</b>的，不是這句查詢讀得到的來源；
    /// 收進來的症狀是這裡把它投影出來的欄位跟真正的來源混在一起列出來。
    /// </remarks>
    [Fact]
    public void 建立它的那一句只看得到真正的來源()
    {
        Assert.Equal(
            new[] { "表 Loan" },
            ScopeColumns("SELECT CopyNo, ReaderId INTO #Loan FROM dbo.Loan WHERE C|"));
    }

    /// <summary>
    /// <c>SELECT … INTO</c> 一樣帶得出展開整句所需的資料。
    /// </summary>
    /// <remarks>
    /// 讀不出的是<b>型別</b>，不是名稱——而 <c>INSERT INTO #Loan</c> 要的正是那份
    /// 名稱清單。沒有型別的欄位在骨架裡填 <c>NULL</c>，那是唯一不會替使用者猜錯
    /// 內容的預留值（<c>SqlInsertStatementText.Literal</c>）。
    ///
    /// 掛的是同一個 <see cref="SqlScriptTable"/>，下游因此一個字都不必分辨。
    /// </remarks>
    [Theory]
    [InlineData("INSERT INTO #L|", CompletionIntent.InsertStatement)]
    [InlineData("MERGE INTO #L|", CompletionIntent.MergeStatement)]
    public void SELECT_INTO帶得出展開整句所需的資料(string tail, CompletionIntent intent)
    {
        var context = Analyze("SELECT CopyNo, ReaderId INTO #Loan FROM dbo.Loan;\r\n" + tail);

        Assert.Equal(intent, context.Intent);
        Assert.True(context.TargetKeywordStart >= 0);

        var suggestion = Assert.Single(context.ScriptSources, item => item.DisplayText == "#Loan");

        Assert.Equal(
            new[] { "CopyNo", "ReaderId" },
            Assert.IsType<SqlScriptTable>(suggestion.Tag).ColumnNames);
    }

    /// <summary>
    /// 使用者實際會寫的樣子：資料來源是一組 <c>VALUES</c>。
    /// </summary>
    /// <remarks>
    /// 選取清單自己就寫出了名稱，<c>FROM</c> 後面是什麼根本不必看。
    /// </remarks>
    [Fact]
    public void 選取清單寫得出名稱時來源是什麼都不必看()
    {
        var context = Analyze(
            "SELECT ID, Name INTO #Temp FROM (VALUES (1, N'Alice'), (2, N'Bob')) AS T(ID, Name);\r\n" +
            "INSERT INTO #T|");

        Assert.Equal(
            new[] { "ID", "Name" },
            Assert.IsType<SqlScriptTable>(
                Assert.Single(context.ScriptSources, item => item.DisplayText == "#Temp").Tag).ColumnNames);
    }

    /// <summary>
    /// 投影不出資料行時掛的是空清單，提交之後退回只補名稱。
    /// </summary>
    /// <remarks>
    /// <c>SELECT *</c> 的名單只有中繼資料知道。空括號的 <c>INSERT</c> 仍然貼得上去，
    /// 那比什麼都不做糟——擋在 <c>SqlCommitExpander</c>，與 <c>SELECT *</c> 不做部分
    /// 展開是同一條理由。
    /// </remarks>
    [Fact]
    public void 投影不出資料行時掛的是空清單()
    {
        var context = Analyze("SELECT * INTO #Loan FROM dbo.Loan;\r\nINSERT INTO #L|");

        Assert.Empty(
            Assert.IsType<SqlScriptTable>(
                Assert.Single(context.ScriptSources, item => item.DisplayText == "#Loan").Tag).ColumnNames);
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
