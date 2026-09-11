using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

public sealed class SqlCompletionContextAnalyzerTests
{
    [Theory]
    [InlineData("SELECT * FROM ", CompletionTarget.DataSource)]
    [InlineData("SELECT * FROM Loans INNER JOIN ", CompletionTarget.DataSource)]
    [InlineData("UPDATE ", CompletionTarget.DataSource)]
    [InlineData("INSERT INTO ", CompletionTarget.DataSource)]
    [InlineData("MERGE INTO ", CompletionTarget.DataSource)]
    [InlineData("MERGE INTO dbo.Loan AS target USING ", CompletionTarget.DataSource)]
    [InlineData("ALTER TABLE ", CompletionTarget.DataSource)]
    [InlineData("DROP TABLE ", CompletionTarget.DataSource)]
    [InlineData("DROP TABLE IF EXISTS ", CompletionTarget.DataSource)]
    [InlineData("TRUNCATE TABLE ", CompletionTarget.DataSource)]
    [InlineData("DROP TRIGGER IF EXISTS ", CompletionTarget.Trigger)]
    [InlineData("ALTER PROCEDURE ", CompletionTarget.Procedure)]
    [InlineData("ALTER PROC ", CompletionTarget.Procedure)]
    [InlineData("ALTER FUNCTION ", CompletionTarget.Function)]
    [InlineData("ALTER VIEW ", CompletionTarget.View)]
    [InlineData("CREATE OR ALTER VIEW ", CompletionTarget.View)]
    [InlineData("ALTER TRIGGER ", CompletionTarget.Trigger)]
    [InlineData("DROP VIEW ", CompletionTarget.View)]
    [InlineData("DROP VIEW IF EXISTS ", CompletionTarget.View)]
    [InlineData("DROP PROCEDURE ", CompletionTarget.Procedure)]
    [InlineData("DROP PROC IF EXISTS ", CompletionTarget.Procedure)]
    [InlineData("DROP FUNCTION ", CompletionTarget.Function)]
    [InlineData("USE ", CompletionTarget.Database)]
    [InlineData("GO\nUSE ", CompletionTarget.Database)]
    public void 依前導關鍵字決定建議目標(string textBeforeCaret, CompletionTarget expected)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(expected, context.Target);
    }

    /// <summary>
    /// 逗號之後仍然在同一個資料來源清單裡。
    /// </summary>
    /// <remarks>
    /// 前導關鍵字只認得游標前一、兩個詞元，而那裡只有一個逗號。判成
    /// <see cref="CompletionTarget.Any"/> 的症狀是空前綴時整個上下文不參與——
    /// <c>FROM dbo.T a, </c> 之後完全沒有清單，使用者要多打一個字才等到一份
    /// 還缺了暫存資料表與 CTE 的名單。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM dbo.T a, ")]
    [InlineData("SELECT * FROM dbo.T a,")]
    [InlineData("UPDATE a\r\nSET a.x = 1\r\nFROM dbo.T a, ")]
    [InlineData("SELECT * FROM (SELECT * FROM dbo.T a, ")]
    public void 逗號之後仍是資料來源(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.DataSource, context.Target);
    }

    /// <summary>
    /// 逗號之後的限定字是結構描述或資料庫，不是別名。
    /// </summary>
    /// <remarks>
    /// 目標留在 <see cref="CompletionTarget.Any"/> 的話，帶語句範圍的多載會拿
    /// <c>LibArchive</c> 去比對別名——而使用者才剛打下的那半個名稱本身就站在
    /// FROM 清單裡，於是比中了自己，清單改列一張不存在的資料表的欄位。
    /// 症狀是「點號之後沒有建議，再多打一個字才有」。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM dbo.T a, LibArchive.|")]
    [InlineData("SELECT * FROM dbo.T a, LibArchive.dbo.|")]
    public void 逗號之後的限定字不當成別名(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.DataSource, context.Target);
        Assert.Null(context.ColumnSources);
        Assert.NotNull(context.QualifierPath);
    }

    /// <summary>
    /// <c>INSERT</c> 的資料行清單不是資料來源清單。
    /// </summary>
    /// <remarks>
    /// 位置分析找子句錨點時會穿過還沒關上的左括號，於是這裡的逗號也拿到資料來源
    /// 的位置。跟著把目標改成資料來源的話，<c>INSERT INTO T (a, </c> 會列出整個
    /// 資料庫的資料表，而那一格文法上只接得了 T 的資料行。
    /// </remarks>
    [Theory]
    [InlineData("INSERT INTO dbo.T (a, ")]
    [InlineData("INSERT INTO dbo.T (a, b, ")]
    public void INSERT的資料行清單不是資料來源(string textBeforeCaret)
    {
        Assert.Equal(
            CompletionTarget.Any,
            SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).Target);
    }

    /// <summary>
    /// 沒有輸入前綴、也沒有可據以縮小範圍的前導關鍵字時不主動跳出清單，
    /// 否則按下空白鍵就會列出整個資料庫。
    /// </summary>
    [Theory]
    [InlineData("SELECT ")]
    [InlineData("WHERE ")]
    [InlineData("  ")]
    public void 既無前綴也無目標時不建議(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.False(context.IsValid);
        Assert.Equal(CompletionTarget.Any, context.Target);
    }

    [Theory]
    [InlineData("ALTER PROCEDURE ", 0, "ALTER")]
    [InlineData("\r\nALTER PROCEDURE usp", 2, "ALTER")]
    [InlineData("SELECT * FROM ", 9, "FROM")]
    [InlineData("SELECT * FROM Loans INNER JOIN pub", 26, "JOIN")]
    public void 回報決定目標的關鍵字起點(string textBeforeCaret, int expectedStart, string expectedKeyword)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.Equal(expectedStart, context.TargetKeywordStart);
        Assert.Equal(
            expectedKeyword,
            textBeforeCaret.Substring(expectedStart, expectedKeyword.Length));
    }

    [Theory]
    [InlineData("EXEC ")]
    [InlineData("EXECUTE ")]
    [InlineData("exec usp")]
    public void EXEC之後只建議Procedure(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.Procedure, context.Target);
    }

    /// <summary>
    /// ALTER PROCEDURE 與 EXEC 後方都只顯示預存程序，但提交行為必須不同：
    /// 前者要放進完整定義，後者要組出一句具名傳值的呼叫。
    /// </summary>
    /// <remarks>
    /// INSERT INTO 與單獨的 INTO 也必須分開。<c>SELECT … INTO #tmp</c> 的 INTO 後面
    /// 是一個還不存在的新名稱，在那裡展開 INSERT 骨架會蓋掉使用者正在取的名字。
    /// </remarks>
    [Theory]
    [InlineData("ALTER PROCEDURE ", CompletionIntent.AlterDefinition)]
    [InlineData("ALTER FUNCTION ", CompletionIntent.AlterDefinition)]
    [InlineData("ALTER TRIGGER ", CompletionIntent.AlterDefinition)]
    [InlineData("ALTER VIEW ", CompletionIntent.AlterDefinition)]
    [InlineData("DROP VIEW ", CompletionIntent.Reference)]
    [InlineData("DROP PROCEDURE ", CompletionIntent.Reference)]
    [InlineData("DROP FUNCTION ", CompletionIntent.Reference)]
    [InlineData("EXEC ", CompletionIntent.ExecuteCall)]
    [InlineData("EXECUTE ", CompletionIntent.ExecuteCall)]
    [InlineData("exec usp", CompletionIntent.ExecuteCall)]
    [InlineData("INSERT INTO ", CompletionIntent.InsertStatement)]
    [InlineData("insert into lo", CompletionIntent.InsertStatement)]
    [InlineData("SELECT * INTO ", CompletionIntent.Reference)]
    [InlineData("SELECT * FROM ", CompletionIntent.Reference)]
    [InlineData("DROP TRIGGER ", CompletionIntent.Reference)]
    [InlineData("SELECT pub", CompletionIntent.Reference)]
    public void 區分提交意圖(string textBeforeCaret, CompletionIntent expected)
    {
        Assert.Equal(expected, SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).Intent);
    }

    /// <remarks>
    /// 提交時要換掉的是整句，起點必須是 INSERT 而不是 INTO——只從 INTO 開始換
    /// 會在編輯器裡留下一個孤零零的 INSERT。
    /// </remarks>
    [Fact]
    public void INSERT_INTO的關鍵字起點落在INSERT上()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("  INSERT INTO ");

        Assert.Equal(2, context.TargetKeywordStart);
        Assert.Equal(CompletionTarget.DataSource, context.Target);
    }

    /// <remarks>
    /// EXEC 之後要列出 sp_executesql、sp_help 這些系統程序，而那份清單掛在
    /// WantsSystemObjects 上；提交行為換成 ExecuteCall 之後那個判斷仍然要成立。
    /// </remarks>
    [Theory]
    [InlineData("EXEC ", true)]
    [InlineData("ALTER PROCEDURE ", false)]
    public void EXEC之後仍然要系統物件(string textBeforeCaret, bool expected)
    {
        Assert.Equal(expected, SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).WantsSystemObjects);
    }

    [Fact]
    public void 沒有目標關鍵字時起點為負一()
    {
        Assert.Equal(-1, SqlCompletionContextAnalyzer.Analyze("SELECT pub").TargetKeywordStart);
    }

    [Fact]
    public void 取出目前輸入的前綴()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("SELECT * FROM libr");

        Assert.Equal("libr", context.Prefix);
        Assert.Equal(14, context.TokenStart);
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.", "dbo")]
    [InlineData("SELECT * FROM [dbo].", "dbo")]
    [InlineData("SELECT * FROM [sales].", "sales")]
    public void 解析Schema限定字(string textBeforeCaret, string expected)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(expected, context.Qualifier);
        Assert.Equal(CompletionTarget.DataSource, context.Target);
    }

    [Theory]
    [InlineData("-- 註解 publ")]
    [InlineData("/* publ")]
    [InlineData("SELECT 'publ")]
    [InlineData("SELECT \"publ")]
    [InlineData("SELECT [publ")]
    public void 字串與註解內不建議(string textBeforeCaret)
    {
        Assert.False(SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).IsValid);
    }

    [Fact]
    public void 字串結束後恢復建議()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("SELECT 'a' FROM publ");

        Assert.True(context.IsValid);
        Assert.Equal("publ", context.Prefix);
    }

    [Fact]
    public void 空白輸入不建議()
    {
        Assert.False(SqlCompletionContextAnalyzer.Analyze(string.Empty).IsValid);
    }

    /// <summary>
    /// 正在打的是一個數值常值時不開清單。
    /// </summary>
    /// <remarks>
    /// T-SQL 的一般識別字不能以數字開頭，所以清單裡沒有一項會是對的。位置分析
    /// 在這裡也幫不上忙——運算子之後一律是 <c>Any</c>，於是整個目錄進場，
    /// 模糊比對把 <c>10</c> 對到 <c>LOG10</c>，而使用者順手按下 Enter 就把數字
    /// 換成了一個函式名稱。與 <c>SqlCompletionTriggers.IsIdentifierLike</c>
    /// 不讓 <c>1.5</c> 的點號彈出物件清單是同一條理由。
    /// </remarks>
    [Theory]
    [InlineData("UPDATE #Loan SET Fine = Fine - 1")]
    [InlineData("UPDATE #Loan SET Fine = Fine - 10")]
    [InlineData("SELECT TOP 1")]
    [InlineData("SELECT * FROM dbo.Loan WHERE ReaderId = 12")]

    // 小數點與十六進位一樣拆得出以數字開頭的詞元。
    [InlineData("SELECT 1.5")]
    [InlineData("SELECT 0x1F")]
    public void 數值常值不建議(string textBeforeCaret)
    {
        Assert.False(SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).IsValid);
    }

    /// <summary>數字只在詞元開頭才算數值常值。</summary>
    /// <remarks>
    /// <c>Cat_BookCopy2</c> 這種名字很常見，而暫存資料表的 <c>#</c> 也在詞元開頭。
    /// 把整個詞元含不含數字當成判準的話，收掉的是使用者真的要補的名稱。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM dbo.Cat_BookCopy2", "Cat_BookCopy2")]
    [InlineData("SELECT * FROM #Loan2", "#Loan2")]
    public void 數字不在開頭時照常建議(string textBeforeCaret, string prefix)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(prefix, context.Prefix);
    }

    /// <summary>
    /// 使用者正在取名字的位置不開清單。
    /// </summary>
    /// <remarks>
    /// 清單裡沒有一項會是對的，而彈出來的唯一效果是使用者順手按下 Enter，
    /// 剛打的 <c>a</c> 被換成 <c>ALTER PROCEDURE</c>——要按復原才救得回來。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM (SELECT 1 AS a) ")]
    [InlineData("SELECT * FROM (SELECT 1 AS a) a")]
    [InlineData("SELECT * FROM t JOIN (SELECT 1 AS a) x")]
    [InlineData("SELECT * FROM (SELECT 1 AS a) AS a")]
    [InlineData("SELECT * FROM dbo.PUBLISHER AS c")]
    [InlineData("SELECT c.PUBL_CODE AS co")]
    [InlineData("DECLARE @pub")]
    [InlineData(";WITH CTE_TEST AS (SELECT 1 AS a)\r\nSELECT * FROM CTE_TEST a")]
    [InlineData("SELECT * FROM CTE_TEST AS a INNER JOIN dbo.Cat_BookCopy b")]
    public void 取名字的位置不建議(string textBeforeCaret)
    {
        Assert.False(SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).IsValid);
    }

    /// <summary>
    /// 換行之後恢復建議，別名寫完之後也是。
    /// </summary>
    /// <remarks>
    /// 這兩個位置分別要接得了下一個子句與下一個敘述。抑制別名清單不能把它們
    /// 一起收掉——那是把一個問題換成另一個問題。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM dbo.PUBLISHER\r\nWHE", "WHERE")]
    [InlineData("SELECT * FROM dbo.PUBLISHER\r\nSEL", "SELECT")]
    [InlineData("SELECT * FROM dbo.PUBLISHER a INN", "INNER")]
    [InlineData("SELECT PublCode FR", "FROM")]
    public void 別名位置之外照常建議關鍵字(string textBeforeCaret, string keyword)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var suggestions = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty);

        Assert.True(context.IsValid);
        Assert.Contains(
            SuggestionMatcher.Filter(suggestions, context),
            suggestion => suggestion.Kind == SuggestionKind.Keyword &&
                suggestion.DisplayText == keyword);
    }

    /// <summary>
    /// <c>AS</c> 後面不是名字的時候，清單照常。
    /// </summary>
    /// <remarks>
    /// 預存程序與檢視的主體開頭就在 <c>AS</c> 之後，那裡少了 <c>BEGIN</c>、
    /// <c>SELECT</c> 的話，這個修正就從一個問題換成另一個問題。
    /// </remarks>
    [Theory]
    [InlineData("CREATE PROCEDURE dbo.p AS BEG")]
    [InlineData("CREATE VIEW v AS SEL")]
    public void AS之後是主體時照常建議(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(SqlKeywordPosition.Any, context.KeywordPosition);
    }

    /// <summary>別名寫完之後就恢復正常，那裡要的是 WHERE、JOIN 這些子句關鍵字。</summary>
    /// <remarks>
    /// 換行之後多出來的 <c>StatementStart</c> 是另一條規則，見
    /// <c>SqlKeywordPositionAnalyzer.AddStatementStartOnNewLine</c>。
    /// </remarks>
    [Fact]
    public void 別名寫完之後恢復建議()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("SELECT * FROM (SELECT 1 AS a) d\r\nWHE");

        Assert.True(context.IsValid);
        Assert.Equal(
            SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.StatementStart,
            context.KeywordPosition);
    }

    /// <summary>
    /// 目前資料庫由連線決定，但「指令碼換過資料庫」只有文字看得出來——
    /// 中繼資料層據此決定要不要重新確認連線。
    /// </summary>
    [Fact]
    public void 上下文帶著指令碼換過的資料庫()
    {
        var input = SqlWithCaret.Parse("USE LibArchive; SELECT * FROM |");

        Assert.Equal(
            "LibArchive",
            SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret).DatabaseSwitch);
    }
}
