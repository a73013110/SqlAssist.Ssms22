using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Snippets;
using SqlAssist.Core.Tests.Completion;
using Xunit;

namespace SqlAssist.Core.Tests.Keywords;

/// <summary>
/// 關鍵字目錄與位置過濾。
/// </summary>
/// <remarks>
/// 目錄本身是產生出來的，因此這裡驗的不是「有沒有列到某個字」，
/// 而是產生器與分析器對得起來：產生器說 DESC 只能出現在 ORDER BY 的欄位之後，
/// 分析器就必須在那個位置回報 OrderByTail，否則 DESC 永遠不會出現。
/// </remarks>
public sealed class SqlKeywordPositionTests
{
    [Fact]
    public void 目錄涵蓋以前手寫清單裡的關鍵字()
    {
        // 換掉手寫清單不能是退步：原本那 51 個字一個都不能少。
        string[] previouslyHandWritten =
        {
            "ALTER", "AND", "AS", "BEGIN", "BY", "CASE", "CREATE", "CROSS",
            "DECLARE", "DELETE", "DISTINCT", "DROP", "ELSE", "END", "EXEC",
            "EXECUTE", "EXISTS", "FROM", "FULL", "FUNCTION", "GROUP", "HAVING",
            "IF", "IN", "INNER", "INSERT", "INTO", "JOIN", "LEFT", "MERGE",
            "NOT", "NULL", "ON", "OR", "ORDER", "OUTER", "PROCEDURE", "RETURN",
            "RIGHT", "SELECT", "SET", "TABLE", "THEN", "TOP", "UNION", "UPDATE",
            "VALUES", "VIEW", "WHEN", "WHERE", "WITH"
        };

        var missing = previouslyHandWritten
            .Where(keyword => !SqlKeywordCatalog.IsKeyword(keyword))
            .ToArray();

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("USE")]
    [InlineData("GO")]
    [InlineData("RESTORE")]
    [InlineData("BACKUP")]
    [InlineData("TRUNCATE")]
    [InlineData("THROW")]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("TRY_CONVERT")]
    [InlineData("IDENTITY_INSERT")]
    public void 目錄補上了手寫清單漏掉的關鍵字(string keyword)
    {
        // 後三個是 camelCase 補底線那一輪撈回來的，最容易在改產生器時掉。
        Assert.True(SqlKeywordCatalog.IsKeyword(keyword));
    }

    [Theory]
    [InlineData("", SqlKeywordPosition.StatementStart)]
    [InlineData("GO ", SqlKeywordPosition.StatementStart)]
    [InlineData("SELECT 1; ", SqlKeywordPosition.StatementStart)]
    [InlineData("SELECT ", SqlKeywordPosition.SelectList)]
    [InlineData("SELECT a ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM ", SqlKeywordPosition.DataSource)]

    // 樣板是 SELECT * FROM t {關鍵字}，但那一行的同一個位置也是別名的位置，
    // 而別名一定寫在同一行——換行之後才是純粹的資料來源尾端。
    // 兩者的分野見「資料來源同一行的下一格可能是別名」。
    // 換行還會多帶一個位元進來，見「換行之後的子句尾端也是下一句的開頭」。
    [InlineData("SELECT * FROM t\r\n",
        SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.StatementStart)]
    [InlineData("SELECT * FROM t WHERE ", SqlKeywordPosition.Predicate)]
    [InlineData("SELECT * FROM t WHERE a = 1 ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t ORDER ", SqlKeywordPosition.ByAnchor)]
    [InlineData("SELECT * FROM t ORDER BY a ", SqlKeywordPosition.OrderByTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t GROUP BY a ", SqlKeywordPosition.GroupByTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t GROUP BY a, b ", SqlKeywordPosition.GroupByTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t GROUP BY ROLLUP(a) ", SqlKeywordPosition.GroupByTail | SqlKeywordPosition.OperandTail)]
    [InlineData("CREATE ", SqlKeywordPosition.DdlObject)]
    [InlineData("BEGIN ", SqlKeywordPosition.BlockStart)]
    [InlineData("SET ", SqlKeywordPosition.SetTarget)]
    [InlineData("INSERT ", SqlKeywordPosition.InsertTarget)]
    public void 分析器認得樣板對應的位置(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 模組標頭的 WITH 與選項清單的逗號之後是那一種模組的選項；本體裡 CTE 的 WITH 不是。
    /// </summary>
    [Theory]
    [InlineData("CREATE PROCEDURE dbo.p WITH ", SqlKeywordPosition.ProcedureOption)]
    [InlineData("CREATE OR ALTER PROC dbo.p @a int = 1, @b AS varchar(10) OUTPUT WITH ", SqlKeywordPosition.ProcedureOption)]
    [InlineData("ALTER PROCEDURE p WITH EXECUTE AS CALLER, ", SqlKeywordPosition.ProcedureOption)]
    [InlineData("CREATE FUNCTION dbo.f (@a int) RETURNS TABLE WITH ", SqlKeywordPosition.FunctionOption)]
    [InlineData("CREATE FUNCTION dbo.f () RETURNS int WITH RETURNS NULL ON NULL INPUT, ", SqlKeywordPosition.FunctionOption)]
    [InlineData("CREATE FUNCTION dbo.f () RETURNS int WITH INLINE = ON, ", SqlKeywordPosition.FunctionOption)]
    [InlineData("CREATE VIEW dbo.v (a, b) WITH ", SqlKeywordPosition.ViewOption)]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan WITH ", SqlKeywordPosition.TriggerOption)]
    [InlineData("CREATE TRIGGER tr ON DATABASE WITH ENCRYPTION, ", SqlKeywordPosition.TriggerOption)]
    public void 模組標頭的WITH之後是選項(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 模組的選項清單寫完之後是本體；函式的參數清單之後是 RETURNS；EXEC、RAISERROR、DBCC、BACKUP／RESTORE 的 WITH
    /// 與登入、使用者、應用程式角色一樣是選項清單，共用 OptionItem。
    /// </summary>
    [Theory]
    [InlineData("CREATE VIEW dbo.v WITH SCHEMABINDING ", SqlKeywordPosition.ModuleHeader)]
    [InlineData("CREATE PROCEDURE dbo.p @a int WITH RECOMPILE, EXECUTE AS OWNER ", SqlKeywordPosition.ModuleHeader)]
    [InlineData("CREATE FUNCTION dbo.f () RETURNS int WITH RETURNS NULL ON NULL INPUT ", SqlKeywordPosition.ModuleHeader)]
    [InlineData("CREATE FUNCTION f () ", SqlKeywordPosition.FunctionReturns)]
    [InlineData("CREATE OR ALTER FUNCTION dbo.fn_Fee (@CopyNo int) ", SqlKeywordPosition.FunctionReturns)]
    [InlineData("ALTER FUNCTION dbo.fn_Fee (@CopyNo int, @Days int = 1) ", SqlKeywordPosition.FunctionReturns)]
    [InlineData("EXEC dbo.usp_Renew @CopyNo = 1, @Due = @d OUTPUT WITH ", SqlKeywordPosition.OptionItem)]
    [InlineData("EXEC dbo.usp_Renew WITH RECOMPILE, ", SqlKeywordPosition.OptionItem)]
    [InlineData("RAISERROR ('x', 16, 1) WITH ", SqlKeywordPosition.OptionItem)]
    [InlineData("RAISERROR (@msg, 16, 1, @CopyNo) WITH NOWAIT, ", SqlKeywordPosition.OptionItem)]
    [InlineData("SELECT a FROM t WHERE b = 1\nRAISERROR ('x', 16, 1) WITH ", SqlKeywordPosition.OptionItem)]
    [InlineData("DBCC CHECKDB WITH ", SqlKeywordPosition.OptionItem)]
    [InlineData("DBCC CHECKDB (N'LibArchive') WITH NO_INFOMSGS, ", SqlKeywordPosition.OptionItem)]
    [InlineData("DBCC CHECKIDENT ('dbo.Lib_Tag', RESEED, 100) WITH ", SqlKeywordPosition.OptionItem)]
    [InlineData("CREATE APPLICATION ROLE LibAppRole WITH ", SqlKeywordPosition.OptionItem)]
    [InlineData("ALTER APPLICATION ROLE LibAppRole WITH NAME = LibAppRole2, ", SqlKeywordPosition.OptionItem)]
    [InlineData("EXECUTE dbo.usp_Renew N'x' WITH ", SqlKeywordPosition.OptionItem)]
    [InlineData("RESTORE LOG d FROM DISK = 'x' WITH NORECOVERY, ", SqlKeywordPosition.OptionItem)]
    [InlineData("CREATE LOGIN LibLogin WITH ", SqlKeywordPosition.OptionItem)]
    [InlineData("CREATE LOGIN LibLogin WITH PASSWORD = 'x' MUST_CHANGE, CHECK_EXPIRATION = ON, ", SqlKeywordPosition.OptionItem)]
    [InlineData("ALTER LOGIN LibLogin WITH NAME = LibLogin2, ", SqlKeywordPosition.OptionItem)]
    [InlineData("CREATE USER LibUser FOR LOGIN LibLogin WITH DEFAULT_SCHEMA = dbo, ", SqlKeywordPosition.OptionItem)]
    [InlineData("BEGIN\n    ALTER USER LibUser WITH NAME = LibUser2,\n        ", SqlKeywordPosition.OptionItem)]
    public void 模組標頭與敘述選項的位置(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// EXEC 的 WITH RESULT SETS 有兩層括號：外層是結果集清單，內層是資料行定義；資料行名稱那一格是新名字。
    /// </summary>
    [Theory]
    [InlineData("EXEC #Lib_RawNames WITH RESULT SETS (", SqlKeywordPosition.ResultSetList, SqlCompletionSlot.Grammar)]
    [InlineData("EXEC ('SELECT 1') WITH RESULT SETS (", SqlKeywordPosition.ResultSetList, SqlCompletionSlot.Grammar)]
    [InlineData("EXEC dbo.usp_Copies WITH RECOMPILE, RESULT SETS ((Branch varchar(10)), ", SqlKeywordPosition.ResultSetList, SqlCompletionSlot.Grammar)]
    [InlineData("EXEC #Lib_RawNames\n    WITH RESULT SETS ((", SqlKeywordPosition.ResultSetColumn, SqlCompletionSlot.Name)]
    [InlineData("EXEC #Lib_RawNames WITH RESULT SETS ((Branch varchar(10), ", SqlKeywordPosition.ResultSetColumn, SqlCompletionSlot.Name)]
    [InlineData("EXEC #Lib_RawNames WITH RESULT SETS ((Branch int), (", SqlKeywordPosition.ResultSetColumn, SqlCompletionSlot.Name)]
    [InlineData("EXEC #Lib_RawNames WITH RESULT SETS ((Branch int ", SqlKeywordPosition.ResultSetColumnTail, SqlCompletionSlot.Grammar)]
    [InlineData("EXEC #Lib_RawNames WITH RESULT SETS ((Branch varchar(10) ", SqlKeywordPosition.ResultSetColumnTail, SqlCompletionSlot.Grammar)]
    [InlineData("EXEC #Lib_RawNames WITH RESULT SETS ((Branch int, Fee decimal(10, 2) ", SqlKeywordPosition.ResultSetColumnTail, SqlCompletionSlot.Grammar)]
    [InlineData("EXEC #Lib_RawNames WITH RESULT SETS ((Branch dbo.Code ", SqlKeywordPosition.ResultSetColumnTail, SqlCompletionSlot.Grammar)]
    [InlineData("EXEC #Lib_RawNames WITH RESULT SETS ((Branch varchar(10) COLLATE Latin1_General_CI_AS ", SqlKeywordPosition.ResultSetColumnTail, SqlCompletionSlot.Grammar)]
    public void 結果集的位置(string textBeforeToken, SqlKeywordPosition expected, SqlCompletionSlot slot)
    {
        var caret = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken);

        Assert.Equal(expected, caret.Keywords);
        Assert.Equal(slot, caret.Slot);
    }

    /// <summary>
    /// 視窗規格的括號自己是一個子句：PARTITION BY 寫完回到它，ORDER BY 寫完是框架。WINDOW 子句一項的名稱
    /// 之後只有 AS，一項寫完之後接逗號或查詢之後的子句。
    /// </summary>
    [Theory]
    [InlineData("SELECT SUM(a) OVER (ByCopy ", SqlKeywordPosition.WindowSpecification, SqlCompletionSlot.Grammar)]
    [InlineData("SELECT SUM(a) OVER (PARTITION BY a, b ", SqlKeywordPosition.WindowSpecification, SqlCompletionSlot.Grammar)]
    [InlineData("SELECT SUM(a) OVER (PARTITION BY ", SqlKeywordPosition.OrderByColumn, SqlCompletionSlot.Grammar)]
    [InlineData("SELECT SUM(a) OVER (PARTITION BY a, ", SqlKeywordPosition.OrderByColumn, SqlCompletionSlot.Grammar)]
    [InlineData("SELECT a FROM t WINDOW ByCopy AS (ByDate ", SqlKeywordPosition.WindowSpecification, SqlCompletionSlot.Grammar)]
    [InlineData("SELECT a FROM t WINDOW ByCopy AS (ORDER BY a DESC ", SqlKeywordPosition.WindowOrderTail | SqlKeywordPosition.OperandTail, SqlCompletionSlot.Grammar)]
    [InlineData("SELECT a FROM t WHERE a = 1 WINDOW ", SqlKeywordPosition.Any, SqlCompletionSlot.Name)]
    [InlineData("SELECT a FROM t WINDOW ByCopy AS (ORDER BY a), ", SqlKeywordPosition.Any, SqlCompletionSlot.Name)]
    [InlineData("SELECT a FROM t WINDOW ByCopy AS (ORDER BY a), [By Date] ", SqlKeywordPosition.WindowName, SqlCompletionSlot.Grammar)]
    [InlineData("SELECT a FROM t GROUP BY a WINDOW ByCopy AS (ORDER BY a) ", SqlKeywordPosition.WindowClauseTail, SqlCompletionSlot.Grammar)]
    public void 視窗的位置(string textBeforeToken, SqlKeywordPosition expected, SqlCompletionSlot slot)
    {
        var caret = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken);

        Assert.Equal(expected, caret.Keywords);
        Assert.Equal(slot, caret.Slot);
    }

    /// <summary>CTE 清單與 CAST 的括號長得像 WINDOW 子句，但不是視窗。</summary>
    [Theory]
    [InlineData(";WITH a AS (SELECT 1 AS x), b ")]
    [InlineData(";WITH a AS (SELECT 1 AS x) ")]
    [InlineData("SELECT CAST(a AS varchar(10)) ")]
    [InlineData("SELECT COUNT(a) OVER (PARTITION BY COALESCE(a ")]
    public void 長得像視窗的格子(string textBeforeToken)
    {
        const SqlKeywordPosition windows = SqlKeywordPosition.WindowSpecification |
            SqlKeywordPosition.WindowName |
            SqlKeywordPosition.WindowClauseTail;

        var caret = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken);

        Assert.True(caret.Keywords == SqlKeywordPosition.Any || (caret.Keywords & windows) == SqlKeywordPosition.None);
    }

    [Theory]
    [InlineData("EXEC #Lib_RawNames WITH RESULT SETS ((Branch int NULL ")]
    [InlineData("EXEC #Lib_RawNames WITH RESULT SETS ((Branch varchar(")]
    [InlineData("SELECT CAST(a AS varchar(10)) ")]
    [InlineData("SELECT * FROM t WHERE a IN ((1), ")]
    public void 不在結果集裡的格子(string textBeforeToken)
    {
        const SqlKeywordPosition resultSets = SqlKeywordPosition.ResultSetList |
            SqlKeywordPosition.ResultSetColumn | SqlKeywordPosition.ResultSetColumnTail;
        var position = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords;

        Assert.True(position == SqlKeywordPosition.Any || (position & resultSets) == SqlKeywordPosition.None, position.ToString());
    }

    [Theory]
    [InlineData("CREATE PROCEDURE p AS WITH ")]
    [InlineData("GRANT EXECUTE ON SCHEMA::dbo TO LibRole WITH ")]
    [InlineData("CREATE VIEW v AS WITH c AS (SELECT 1 AS a), ")]
    [InlineData("CREATE INDEX i ON t (a) WITH ")]
    [InlineData("ALTER TABLE t WITH ")]
    [InlineData("EXECUTE AS LOGIN = 'LibLogin' WITH ")]
    [InlineData("ALTER ROLE LibRole WITH ")]
    [InlineData("SELECT a, ")]
    [InlineData("ALTER USER LibUser WITH NAME = LibUser2; SELECT a, ")]
    [InlineData("EXEC dbo.usp_Copies WITH RESULT SETS (AS FOR XML ")]
    [InlineData("SELECT a FROM dbo.Loan WITH ")]
    public void 模組本體與別的敘述的WITH不是模組選項(string textBeforeToken)
    {
        const SqlKeywordPosition options = SqlKeywordPosition.ProcedureOption | SqlKeywordPosition.FunctionOption |
            SqlKeywordPosition.ViewOption | SqlKeywordPosition.TriggerOption |
            SqlKeywordPosition.OptionItem;
        var position = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords;

        // 判不出位置（Any）也含這幾個位元，那不是判成選項。
        Assert.True(position == SqlKeywordPosition.Any || (position & options) == SqlKeywordPosition.None, position.ToString());
    }

    /// <summary>
    /// 往回找子句關鍵字時，一整組括號要當成一個運算元跳過去。
    /// </summary>
    /// <remarks>
    /// 走進括號裡撈到的是子查詢自己的子句：<c>FROM (… ON a = b) x</c> 會判成
    /// 「JOIN 條件之後」，於是 WHERE 從清單裡消失，而那正是使用者寫完衍生資料表
    /// 之後要打的第一個字。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM (SELECT 1 AS a FROM t WHERE x = 1) d ", SqlKeywordPosition.TableSourceTail)]
    [InlineData("SELECT * FROM (SELECT 1 AS a) d JOIN u ON d.a = u.a ",
        SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t WHERE (a = 1) ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t WHERE x IN (SELECT y FROM u) ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT COUNT(*) ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.FunctionCallTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t ORDER BY SUM(a) ", SqlKeywordPosition.OrderByTail | SqlKeywordPosition.FunctionCallTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT dbo.fn_Fee(a) ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT STRING_AGG(a, ',') WITHIN GROUP (ORDER BY a) ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.FunctionCallTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT b, PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY a DESC) ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.FunctionCallTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t WHERE COALESCE(a, 1) ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t WITH (NOLOCK) ", SqlKeywordPosition.TableSourceTail)]
    [InlineData("SELECT * FROM (t1 JOIN t2 ON t1.x = t2.x) ", SqlKeywordPosition.TableSourceTail)]
    [InlineData("INSERT INTO t (a, b) ", SqlKeywordPosition.TableSourceTail)]
    [InlineData(";WITH c AS (SELECT 1 AS a) ", SqlKeywordPosition.StatementStart)]
    public void 括號是一個運算元不是一段路(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 文法強制別名的右括號後面是別名或 <c>AS</c>：衍生資料表與 PIVOT、UNPIVOT。
    /// </summary>
    /// <remarks>
    /// <c>FROM (SELECT 1)</c> 少了別名就是語法錯誤，與 <c>FROM dbo.T </c> 一樣是可能是名字的格子：
    /// 列得出 <c>AS</c>，軟選讓 Enter 保住別名。<c>AS</c> 之後才一定是名字。
    /// 括號是什麼由它<b>前面</b>那個字決定：同樣裝著一個 SELECT，
    /// 接在 <c>IN</c> 後面的那個是運算式，後面不接別名。換行也不改變這件事。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM (SELECT 1 AS a) ", SqlCompletionSlot.MaybeName)]
    [InlineData("SELECT * FROM t JOIN (SELECT 1 AS a) ", SqlCompletionSlot.MaybeName)]
    [InlineData("SELECT * FROM t CROSS APPLY (SELECT 1 AS a) ", SqlCompletionSlot.MaybeName)]
    [InlineData("SELECT * FROM ((SELECT 1 AS a)) ", SqlCompletionSlot.MaybeName)]
    [InlineData("SELECT * FROM (VALUES (1), (2)) ", SqlCompletionSlot.MaybeName)]
    [InlineData("MERGE dbo.T AS t USING (SELECT 1 AS a) ", SqlCompletionSlot.MaybeName)]
    [InlineData("SELECT * FROM (SELECT 1)\n", SqlCompletionSlot.MaybeName)]
    [InlineData("SELECT * FROM t PIVOT (SUM(x) FOR y IN ([a])) ", SqlCompletionSlot.MaybeName)]
    [InlineData("SELECT * FROM t UNPIVOT (v FOR y IN (a, b)) ", SqlCompletionSlot.MaybeName)]
    [InlineData("SELECT * FROM (SELECT 1 AS a) AS ", SqlCompletionSlot.Name)]
    [InlineData("MERGE dbo.T AS t USING (SELECT 1 AS a) AS ", SqlCompletionSlot.Name)]
    [InlineData("SELECT * FROM t PIVOT (SUM(x) FOR y IN ([a])) AS ", SqlCompletionSlot.Name)]
    public void 必填別名的括號之後是別名或AS(string textBeforeToken, SqlCompletionSlot slot)
    {
        var caret = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken);

        Assert.Equal(slot, caret.Slot);
        Assert.Equal(SqlKeywordPosition.TableSourceTail, caret.Keywords);
    }

    /// <summary>
    /// <c>AS</c> 後面接的是不是名字，看的是它<b>前面</b>。
    /// </summary>
    /// <remarks>
    /// 一個運算式或一個資料來源剛寫完，後面就是別名；其餘的 <c>AS</c> 接的是
    /// 主體、型別或執行身分，那些位置清單照常。分不出來的話兩種都會壞：
    /// 一邊是別名被清單換掉，另一邊是預存程序主體開頭打不出 BEGIN。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM (SELECT 1 AS a) AS ", true)]
    [InlineData("SELECT * FROM dbo.PUBLISHER AS ", true)]
    [InlineData("SELECT * FROM a JOIN b AS ", true)]
    [InlineData("SELECT * FROM a CROSS APPLY dbo.fn(1) AS ", true)]
    [InlineData("SELECT x.PUBL_CODE AS ", true)]
    [InlineData("SELECT x.a, x.b AS ", true)]
    [InlineData("SELECT 1 AS\n", true)]
    [InlineData("CREATE PROCEDURE dbo.p AS ", false)]
    [InlineData("CREATE PROCEDURE dbo.p @a int AS ", false)]
    [InlineData("CREATE VIEW v AS ", false)]
    [InlineData("CREATE FUNCTION f() RETURNS TABLE AS ", false)]
    [InlineData("CREATE TRIGGER t ON dbo.T AFTER INSERT AS ", false)]
    [InlineData("EXECUTE AS ", false)]
    public void AS之後是別名還是別的東西(string textBeforeToken, bool isAlias)
    {
        var caret = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken);

        Assert.Equal(isAlias ? SqlCompletionSlot.Name : SqlCompletionSlot.Grammar, caret.Slot);
    }

    /// <summary>
    /// 模組標頭的 <c>AS</c> 之後是主體：一句的開頭。
    /// </summary>
    /// <remarks>
    /// 以前這裡判不出位置，主體開頭的 <c>SET NOCOUNT </c> 因此只能以「可能」的身分列出
    /// ON／OFF，整份目錄跟著進場。標頭本身的 AS（<c>EXECUTE AS</c>、參數的 <c>@a AS int</c>）
    /// 不算，走出標頭之後的 AS 也不算。
    /// </remarks>
    [Theory]
    [InlineData("CREATE PROCEDURE dbo.p AS ", SqlKeywordPosition.StatementStart)]
    [InlineData("CREATE PROCEDURE dbo.p @a int AS\n", SqlKeywordPosition.StatementStart)]
    [InlineData("CREATE PROCEDURE p @a AS int AS ", SqlKeywordPosition.StatementStart)]
    [InlineData("CREATE PROC p WITH EXECUTE AS OWNER AS ", SqlKeywordPosition.StatementStart)]
    [InlineData("CREATE OR ALTER VIEW v (a, b) AS ", SqlKeywordPosition.StatementStart)]
    [InlineData("ALTER FUNCTION f (@a int) RETURNS @t TABLE (a int) AS ", SqlKeywordPosition.StatementStart)]
    [InlineData("CREATE TRIGGER t ON dbo.T AFTER INSERT, UPDATE AS ", SqlKeywordPosition.StatementStart)]
    [InlineData("EXECUTE AS ", SqlKeywordPosition.Any)]
    [InlineData("CREATE PROC p WITH EXECUTE AS ", SqlKeywordPosition.Any)]
    [InlineData("CREATE PROCEDURE p @a AS ", SqlKeywordPosition.Any)]
    [InlineData("CREATE TYPE dbo.T AS ", SqlKeywordPosition.Any)]
    [InlineData("ALTER TABLE t ADD c AS ", SqlKeywordPosition.Any)]
    [InlineData("CREATE PROCEDURE p AS BEGIN EXECUTE AS ", SqlKeywordPosition.Any)]
    public void 模組標頭的AS之後是主體開頭(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 資料來源之後、別名還沒寫，而且沒有換行——那一格可能是別名。
    /// </summary>
    /// <remarks>
    /// 沒有 <c>AS</c> 的別名與打到一半的子句關鍵字在剖析器眼中一模一樣，
    /// 唯一分得開的線索是換行：別名一定寫在資料來源的同一行，
    /// 而子句與下一個敘述幾乎總是換行寫。別名寫完之後接的是資料來源尾端。
    ///
    /// 資料來源的後綴屬於同一項：別名寫在 <c>FOR PATH</c>、<c>FOR SYSTEM_TIME …</c> 與資料表值函式的
    /// <c>WITH (…)</c> 資料行結構描述之後。MERGE 的目標與 USING 的來源同一條規則。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM CTE_TEST ")]
    [InlineData("SELECT * FROM dbo.PUBLISHER ")]
    [InlineData("SELECT * FROM a INNER JOIN dbo.Cat_BookCopy ")]
    [InlineData("SELECT * FROM dbo.a, dbo.b ")]
    [InlineData("SELECT * FROM [dbo].[PUBLISHER] ")]
    [InlineData("SELECT * FROM dbo.fn_Loans(1) ")]
    [InlineData("SELECT * FROM a CROSS APPLY OPENJSON(a.Doc) ")]
    [InlineData("SELECT * FROM @rows ")]
    [InlineData("SELECT * FROM dbo.PUBLISHER /* 同一行 */ ")]
    [InlineData("SELECT * FROM OPENJSON(@j) WITH (a int) ")]
    [InlineData("SELECT * FROM t CROSS APPLY OPENJSON(t.Doc) WITH (a int '$.a') ")]
    [InlineData("SELECT * FROM t FOR SYSTEM_TIME AS OF '2020-01-01' ")]
    [InlineData("SELECT * FROM t FOR SYSTEM_TIME FROM @a TO @b ")]
    [InlineData("SELECT * FROM t FOR SYSTEM_TIME BETWEEN '2020' AND '2021' ")]
    [InlineData("SELECT * FROM t FOR SYSTEM_TIME CONTAINED IN ('2020', '2021') ")]
    [InlineData("SELECT * FROM t FOR SYSTEM_TIME ALL ")]
    [InlineData("SELECT * FROM dbo.Loan FOR PATH ")]
    [InlineData("MERGE dbo.Loan ")]
    [InlineData("MERGE INTO dbo.Loan ")]
    [InlineData("MERGE dbo.Loan AS t USING dbo.LoanDetail ")]
    [InlineData("DELETE l FROM dbo.Loan ")]
    [InlineData("DELETE l FROM dbo.Loan l JOIN dbo.Copy ")]
    public void 資料來源同一行的下一格可能是別名(string textBeforeToken)
    {
        var caret = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken);

        Assert.Equal(SqlCompletionSlot.MaybeName, caret.Slot);
        Assert.Equal(SqlKeywordPosition.TableSourceTail, caret.Keywords);
    }

    /// <summary>
    /// 別名寫完、或者游標已經換行，就恢復成一般的資料來源尾端。
    /// </summary>
    /// <remarks>
    /// 這裡每一項都是「猜錯就打不出來」的字：換行之後要接得了 WHERE 與下一個
    /// SELECT，別名寫完之後要接得了 INNER。少了任何一項，這個修正就從一個問題
    /// 換成另一個問題。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM CTE_TEST a ", SqlKeywordPosition.TableSourceTail)]
    [InlineData("SELECT * FROM CTE_TEST AS a ", SqlKeywordPosition.TableSourceTail)]
    [InlineData("SELECT * FROM dbo.T WITH (NOLOCK) ", SqlKeywordPosition.TableSourceTail)]

    // 換行多出來的那個位元是「下一句可以開始了」，見
    // 「換行之後的子句尾端也是下一句的開頭」。資料來源尾端一個都沒少，
    // 這個測試守的仍然是同一件事。
    [InlineData("SELECT * FROM dbo.PUBLISHER\r\n",
        SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.StatementStart)]
    [InlineData("SELECT * FROM dbo.PUBLISHER\n",
        SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.StatementStart)]
    public void 別名寫完或換行之後恢復成資料來源尾端(
        string textBeforeToken,
        SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 選取清單與資料來源是同一條別名規則。
    /// </summary>
    /// <remarks>
    /// 一項寫完、同一行、前面沒有緊鄰另一個運算元，下一格就可能是別名。關鍵字位置
    /// 仍然是選取清單尾端：<c>SELECT PublCode FROM …</c> 是最常打的一行，
    /// 可能是名字的那一格只是改成軟選，<c>FROM</c> 一個字都沒少。
    /// </remarks>
    [Theory]
    [InlineData("SELECT PublCode ")]
    [InlineData("SELECT a, b ")]
    [InlineData("SELECT a + b ")]
    [InlineData("SELECT dbo.fn_Fee(a) ")]
    [InlineData("SELECT N'x' ")]
    [InlineData("SELECT CASE WHEN a = 1 THEN 2 END ")]
    [InlineData("SELECT DISTINCT a ")]
    [InlineData("SELECT TOP (1) a ")]
    [InlineData("SELECT * FROM (SELECT a ")]
    public void 選取清單同一行的下一格也可能是別名(string textBeforeToken)
    {
        var caret = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken);

        Assert.Equal(SqlCompletionSlot.MaybeName, caret.Slot);
        Assert.Equal(SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail, caret.Keywords);
    }

    /// <summary>
    /// 別名已經寫了、這一項根本不接別名，或者游標不在清單這一層。
    /// </summary>
    /// <remarks>
    /// <c>SELECT a b </c> 的 <c>b</c> 前面緊鄰一個運算元，那就是別名；
    /// <c>*</c> 後面不能接別名；函式引數與 CASE 的裡面不是選取清單這一層。
    /// </remarks>
    [Theory]
    [InlineData("SELECT a b ")]
    [InlineData("SELECT a AS b ")]
    [InlineData("SELECT t.* ")]
    [InlineData("SELECT COUNT(a ")]
    [InlineData("SELECT CASE WHEN a = 1 THEN b ")]
    [InlineData("SELECT a\n")]
    [InlineData("SELECT * FROM dbo.T WITH (NOLOCK) ")]
    [InlineData("SELECT * FROM (t1 JOIN t2 ON t1.x = t2.x) ")]
    [InlineData("INSERT INTO dbo.T ")]
    [InlineData("SELECT * FROM dbo.Loan\n/* 接續 */ ")]
    [InlineData("SELECT * FROM t PIVOT (SUM(x) FOR y IN ([a])) p ")]
    [InlineData("SELECT * FROM OPENJSON(@j) WITH (a int) j ")]
    [InlineData("SELECT * FROM t FOR SYSTEM_TIME ALL h ")]
    [InlineData("SELECT * FROM t FOR SYSTEM_TIME AS ")]
    [InlineData("MERGE dbo.Loan AS t USING dbo.LoanDetail s ")]

    // DELETE 與 FETCH 自己的 FROM 後面是動詞的目標，文法不接別名。
    [InlineData("DELETE FROM dbo.Loan ")]
    [InlineData("DELETE TOP (5) FROM dbo.Loan ")]
    [InlineData("FETCH NEXT FROM c ")]
    [InlineData("DECLARE c CURSOR FOR SELECT 1; FETCH ABSOLUTE @n FROM c ")]
    [InlineData("EXEC dbo.p @a = 1\nDELETE FROM dbo.Loan ")]
    [InlineData("SELECT a FROM t WHERE b = 1 DELETE FROM dbo.Loan ")]
    public void 不是別名位置的照常硬選(string textBeforeToken)
    {
        Assert.Equal(SqlCompletionSlot.Grammar, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Slot);
    }

    /// <summary>
    /// TOP 子句之後是選取清單的起點。
    /// </summary>
    /// <remarks>
    /// 把 TOP 的引數當成清單的第一項，<c>SELECT TOP 10 </c> 就判成尾端，列的是 FROM 而
    /// 不是 CASE 與欄位——而且那一格還會被當成 <c>10</c> 的別名。
    /// INSERT／DELETE 的 TOP 不是這一條。PERCENT、WITH TIES 掛在自己的位置，
    /// 寫完 WITH TIES 就不再接。
    /// </remarks>
    [Theory]
    [InlineData("SELECT TOP 10 ", SqlKeywordPosition.SelectList | SqlKeywordPosition.TopClauseTail)]
    [InlineData("SELECT TOP (10) ", SqlKeywordPosition.SelectList | SqlKeywordPosition.TopClauseTail)]
    [InlineData("SELECT TOP 10 PERCENT ", SqlKeywordPosition.SelectList | SqlKeywordPosition.TopClauseTail)]
    [InlineData("SELECT TOP 10 WITH TIES ", SqlKeywordPosition.SelectList)]
    [InlineData("SELECT DISTINCT TOP (@n) PERCENT WITH TIES ", SqlKeywordPosition.SelectList)]
    [InlineData("SELECT TOP 10 a ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("DELETE TOP (10) ", SqlKeywordPosition.Any)]
    public void TOP子句之後是選取清單起點(string textBeforeToken, SqlKeywordPosition expected)
    {
        var caret = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken);

        Assert.Equal(expected, caret.Keywords);
    }

    /// <summary>
    /// 寫完的 CASE 是一個運算元；還沒寫完的 CASE 是游標所在的那一層。
    /// </summary>
    /// <remarks>
    /// 以前 CASE 的裡面一律判成外層子句的尾端，<c>THEN b </c> 之後列的是 FROM 而沒有
    /// ELSE、END；<c>END </c> 之後則因為 END 是認得但沒有位置的字而整個放行。
    /// 括號裡的另一個運算式不受外層 CASE 影響；述詞位置上的括號（<c>AND (</c>）不是另一個運算式。
    /// </remarks>
    [Theory]
    [InlineData("SELECT CASE WHEN a ", SqlKeywordPosition.CaseArm | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT CASE WHEN a = 1 ", SqlKeywordPosition.CaseArm | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT CASE WHEN a = 1 AND b IN (1, 2) ", SqlKeywordPosition.CaseArm | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT CASE a WHEN 1 ", SqlKeywordPosition.CaseArm | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT CASE WHEN a = 1 THEN b ", SqlKeywordPosition.CaseBody | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT CASE WHEN a = 1 THEN b ELSE c ", SqlKeywordPosition.CaseBody | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT CASE a ", SqlKeywordPosition.CaseBody | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT CASE WHEN a = 1 THEN CASE WHEN b = 2 THEN 3 END ", SqlKeywordPosition.CaseBody | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT CASE WHEN a = 1 THEN 2 END ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t WHERE x = CASE WHEN a = 1 THEN 2 END ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT CASE WHEN a IN (1, ", SqlKeywordPosition.Any)]
    [InlineData("SELECT CASE WHEN a = 1 AND (b = 2 OR c ", SqlKeywordPosition.CaseArm | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT CASE WHEN COALESCE(a ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("BEGIN SELECT 1 END ", SqlKeywordPosition.BlockEnd | SqlKeywordPosition.StatementStart)]
    [InlineData("IF @a = 1 PRINT 'x' ELSE PRINT 'y' ", SqlKeywordPosition.Any)]
    public void CASE的位置(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// <c>IIF(</c> 的第一個引數是述詞，位置與 WHERE 相同；逗號之後的引數是一般的值。
    /// </summary>
    /// <remarks>以前穿出去借選取清單尾端，比較寫完打不出 AND、OR，運算元之後打不出 IS。</remarks>
    [Theory]
    [InlineData("SELECT IIF(Fee > 2 ", "AND", true)]
    [InlineData("SELECT IIF(@Due ", "IS", true)]
    [InlineData("SELECT IIF(l.Fee > 2 OR l.Fee ", "IS", true)]
    [InlineData("SELECT IIF((Fee > 2 ", "OR", true)]
    [InlineData("SELECT a FROM t WHERE IIF(Fee ", "BETWEEN", true)]
    [InlineData("SELECT IIF(Fee > 2, 1 ", "AND", false)]
    [InlineData("SELECT IIF(Fee > 2, 1, 0) ", "AND", false)]
    public void IIF的第一個引數是述詞(string textBeforeToken, string keyword, bool expected)
    {
        Assert.Equal(expected, AllowedAt(textBeforeToken, keyword));
    }

    /// <summary>
    /// 使用者正要取新名字的那一格：建立敘述、CTE 與 SELECT … INTO。
    /// </summary>
    /// <remarks>
    /// <c>CREATE OR ALTER</c> 也常是既有物件，所以只是「可能是名字」；那個位置也接得上字的
    /// （<c>CREATE SCHEMA AUTHORIZATION</c>、<c>CREATE DATABASE ENCRYPTION KEY</c>）同樣只是可能。
    /// 名稱寫得出結構描述的種類，第一段也只是可能（<c>CREATE PROCEDURE dbo.p</c> 的 <c>dbo</c>），點號之後才是新名字。
    /// <c>ALTER</c>、<c>INSERT INTO</c> 要的是既有物件，照常。
    /// WITH 只認敘述開頭的那一個：資料表提示與模組選項的 WITH 不是 CTE。
    /// </remarks>
    [Theory]
    [InlineData("CREATE PROCEDURE ", SqlCompletionSlot.MaybeName)]
    [InlineData("CREATE PROC dbo.", SqlCompletionSlot.Name)]
    [InlineData("CREATE VIEW [dbo].", SqlCompletionSlot.Name)]
    [InlineData("CREATE TABLE ", SqlCompletionSlot.MaybeName)]
    [InlineData("CREATE TABLE LibArchive.dbo.", SqlCompletionSlot.Name)]
    [InlineData("CREATE UNIQUE CLUSTERED INDEX ", SqlCompletionSlot.Name)]
    [InlineData("CREATE CERTIFICATE ", SqlCompletionSlot.Name)]
    [InlineData("CREATE SYMMETRIC KEY ", SqlCompletionSlot.Name)]
    [InlineData("CREATE SCHEMA ", SqlCompletionSlot.MaybeName)]
    [InlineData("CREATE DATABASE ", SqlCompletionSlot.MaybeName)]
    [InlineData("CREATE OR ALTER FUNCTION ", SqlCompletionSlot.MaybeName)]
    [InlineData("CREATE OR ALTER VIEW dbo.", SqlCompletionSlot.MaybeName)]
    [InlineData("ALTER PROCEDURE ", SqlCompletionSlot.Grammar)]
    [InlineData("CREATE ", SqlCompletionSlot.Grammar)]
    [InlineData("CREATE FUNCTION f() RETURNS TABLE AS ", SqlCompletionSlot.Grammar)]
    [InlineData("WITH ", SqlCompletionSlot.Name)]
    [InlineData(";WITH ", SqlCompletionSlot.Name)]
    [InlineData("SELECT 1;\nWITH ", SqlCompletionSlot.Name)]
    [InlineData("GO\nWITH ", SqlCompletionSlot.Name)]
    [InlineData("BEGIN WITH ", SqlCompletionSlot.Name)]
    [InlineData("CREATE VIEW v AS WITH ", SqlCompletionSlot.Name)]
    [InlineData(";WITH a AS (SELECT 1 AS x), ", SqlCompletionSlot.Name)]
    [InlineData(";WITH a (x) AS (SELECT 1), b AS (SELECT 2 AS y), ", SqlCompletionSlot.Name)]
    [InlineData("SELECT * FROM t WITH ", SqlCompletionSlot.Grammar)]
    [InlineData("CREATE VIEW v WITH ", SqlCompletionSlot.Grammar)]
    [InlineData("SELECT * FROM t WHERE a IN (1, 2), ", SqlCompletionSlot.Grammar)]
    [InlineData("SELECT a INTO ", SqlCompletionSlot.Name)]
    [InlineData("SELECT TOP 10 * INTO ", SqlCompletionSlot.Name)]
    [InlineData("INSERT INTO ", SqlCompletionSlot.Grammar)]
    [InlineData("MERGE INTO ", SqlCompletionSlot.Grammar)]
    [InlineData("FETCH NEXT FROM c INTO ", SqlCompletionSlot.Grammar)]
    [InlineData("DELETE FROM t OUTPUT deleted.a INTO ", SqlCompletionSlot.Grammar)]
    public void 新名字的位置(string textBeforeToken, SqlCompletionSlot expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Slot);
    }

    /// <summary>
    /// 新名字的第一段可能是結構描述：只在名稱寫得出結構描述、還沒寫限定字、而那一格不是既有物件時。
    /// </summary>
    /// <remarks>
    /// <c>CREATE OR ALTER</c> 的那一格照目標列既有物件，結構描述已在其中，不走這一條。
    /// </remarks>
    [Theory]
    [InlineData("CREATE PROCEDURE ", true)]
    [InlineData("CREATE PROC ", true)]
    [InlineData("CREATE VIEW ", true)]
    [InlineData("CREATE FUNCTION ", true)]
    [InlineData("CREATE TYPE ", true)]
    [InlineData("CREATE SEQUENCE ", true)]
    [InlineData("CREATE SYNONYM ", true)]
    [InlineData("CREATE TRIGGER ", true)]
    [InlineData("CREATE PROCEDURE dbo.", false)]
    [InlineData("CREATE OR ALTER PROCEDURE ", false)]
    [InlineData("CREATE UNIQUE CLUSTERED INDEX ", false)]
    [InlineData("CREATE STATISTICS ", false)]
    [InlineData("CREATE LOGIN ", false)]
    [InlineData("CREATE SCHEMA ", false)]
    [InlineData("SELECT a INTO ", false)]
    public void 新名字的限定字(string textBeforeToken, bool expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).NewNameQualifier);
    }

    /// <summary>
    /// 資料行定義的每一項開頭：新資料行名稱，或 CONSTRAINT、PRIMARY KEY 這些字。
    /// </summary>
    /// <remarks>
    /// 與 <c>ALTER TABLE t ADD </c> 是同一條規則，但不是同一個位置：兩者接得了的
    /// 關鍵字不一樣，見 <see cref="資料行定義的起點只列條件約束關鍵字"/>。
    /// </remarks>
    [Theory]
    [InlineData("CREATE TABLE dbo.t (", SqlKeywordPosition.ColumnDefinition)]
    [InlineData("CREATE TABLE #t (a INT, ", SqlKeywordPosition.ColumnDefinition)]
    [InlineData("CREATE TABLE t (a INT NOT NULL,\n    ", SqlKeywordPosition.ColumnDefinition)]
    [InlineData("DECLARE @t TABLE (", SqlKeywordPosition.ColumnDefinition)]
    [InlineData("DECLARE @t AS TABLE (a INT, ", SqlKeywordPosition.ColumnDefinition)]
    [InlineData("CREATE TYPE dbo.T AS TABLE (", SqlKeywordPosition.ColumnDefinition)]
    [InlineData("CREATE FUNCTION f() RETURNS @t TABLE (", SqlKeywordPosition.ColumnDefinition)]
    [InlineData("ALTER TABLE t ADD ", SqlKeywordPosition.AlterTableAdd)]
    [InlineData("CREATE TABLE Shelf (Kind int CONSTRAINT UqKind UNIQUE WITH FILLFACTOR = 80 ON Archive, ", SqlKeywordPosition.ColumnDefinition)]
    public void 資料行定義的起點可能是名字(string textBeforeToken, SqlKeywordPosition expected)
    {
        var caret = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken);

        Assert.Equal(SqlCompletionSlot.MaybeName, caret.Slot);
        Assert.Equal(expected, caret.Keywords);
    }

    /// <summary>
    /// 資料行定義寫完型別或計算資料行的運算式之後，以及之後每一個寫完的選項之後，是同一個位置；
    /// CREATE TABLE、資料表變數、ALTER TABLE ADD／ALTER COLUMN 與 OPENJSON 的 WITH 共用。
    /// </summary>
    [Theory]
    [InlineData("CREATE TABLE t (a int ")]
    [InlineData("CREATE TABLE t (Code char(10) ")]
    [InlineData("CREATE TABLE t (a varchar(10) COLLATE Latin1_General_CI_AS ")]
    [InlineData("CREATE TABLE t (a nvarchar(10) MASKED WITH (FUNCTION = 'default()') ")]
    [InlineData("CREATE TABLE t (a int NOT NULL ")]
    [InlineData("CREATE TABLE t (a int IDENTITY(1, 1) ")]
    [InlineData("CREATE TABLE t (a national char varying(10) ")]
    [InlineData("CREATE TABLE t (a double precision ")]
    [InlineData("CREATE TABLE t (Neg AS (Fee * 2) PERSISTED ")]
    [InlineData("CREATE TABLE t ([Code] [char](10) ")]
    [InlineData("CREATE TABLE t (Kind int CONSTRAINT UqKind UNIQUE WITH FILLFACTOR = 80 ON Archive ")]
    [InlineData("DECLARE @t TABLE (a int ")]
    [InlineData("ALTER TABLE t ADD a int ")]
    [InlineData("ALTER TABLE t ADD a int NULL, b xml ")]
    [InlineData("ALTER TABLE t ALTER COLUMN a varbinary(max) ")]
    [InlineData("SELECT * FROM OPENJSON(@j) WITH (a int '$.a' ")]
    public void 資料行型別之後是共用的尾巴(string textBeforeToken)
    {
        Assert.Equal(SqlKeywordPosition.ColumnDefinitionTail, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 資料行的預設值與計算運算式寫完，同時是運算元之後：與 WHERE 裡的運算元一樣接 AT TIME ZONE。
    /// </summary>
    /// <remarks>型別、<c>NULL</c> 與 <c>CHECK (…)</c> 之後不是，見上一個測試。</remarks>
    [Theory]
    [InlineData("CREATE TABLE t (Neg AS -Fee ")]
    [InlineData("CREATE TABLE t (a datetimeoffset DEFAULT @d ")]
    [InlineData("CREATE TABLE t (a datetime DEFAULT (GETDATE()) ")]
    [InlineData("DECLARE @t TABLE (a int, b datetimeoffset NOT NULL DEFAULT @d AT TIME ZONE @z ")]
    [InlineData("DECLARE @t TABLE (a int, b AS a AT TIME ZONE @z ")]
    public void 資料行的預設值與計算運算式之後是運算元之後(string textBeforeToken)
    {
        Assert.Equal(
            SqlKeywordPosition.ColumnDefinitionTail | SqlKeywordPosition.OperandTail,
            SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 運算式裡的運算元寫完，不論在哪個子句都疊上運算元之後；函式呼叫與 <c>NEXT VALUE FOR</c> 另疊函式呼叫之後。
    /// </summary>
    /// <remarks>資料來源、型別這些名稱不是運算元，不疊。</remarks>
    [Theory]
    [InlineData("SELECT * FROM t WHERE b < c ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t WHERE c AT TIME ZONE 'UTC' ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t WHERE c > DATEADD(day, 1, b ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT a FROM t ORDER BY c ", SqlKeywordPosition.OrderByTail | SqlKeywordPosition.OperandTail)]
    [InlineData("UPDATE t SET c = b ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.UpdateSetTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT NEXT VALUE FOR s ",
        SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail | SqlKeywordPosition.FunctionCallTail)]
    [InlineData("SELECT NEXT VALUE FOR dbo.s ",
        SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail | SqlKeywordPosition.FunctionCallTail)]
    [InlineData("SELECT * FROM t ", SqlKeywordPosition.TableSourceTail)]
    [InlineData("CREATE TABLE t (a int CHECK (a > 1) ", SqlKeywordPosition.ColumnDefinitionTail)]
    public void 運算元寫完疊上運算元之後(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>還沒寫完型別、停在要接運算式或名稱的字上，或不是資料行的一項：不是型別之後的尾巴。</summary>
    [Theory]
    [InlineData("CREATE TABLE t (a ")]
    [InlineData("CREATE TABLE t (a int DEFAULT ")]
    [InlineData("CREATE TABLE t (a int CHECK (a > ")]
    [InlineData("CREATE TABLE t (a int REFERENCES u (a) ")]
    [InlineData("CREATE TABLE t (PRIMARY KEY (a) ")]
    [InlineData("CREATE TABLE t (PERIOD FOR SYSTEM_TIME (a, b) ")]
    [InlineData("CREATE TABLE t (a int) ")]
    [InlineData("INSERT INTO t (a, b ")]
    [InlineData("ALTER TABLE t ALTER COLUMN a ADD PERSISTED ")]
    public void 不是型別之後的尾巴(string textBeforeToken)
    {
        Assert.NotEqual(SqlKeywordPosition.ColumnDefinitionTail, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>型別的括號與 INSERT 的資料行清單不是資料行定義。</summary>
    [Theory]
    [InlineData("CREATE TABLE t (a DECIMAL(10, ")]
    [InlineData("INSERT INTO t (")]
    [InlineData("INSERT INTO t (a, ")]
    public void 型別括號與INSERT清單不是資料行定義(string textBeforeToken)
    {
        var caret = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken);

        Assert.NotEqual(SqlCompletionSlot.MaybeName, caret.Slot);
        Assert.NotEqual(SqlKeywordPosition.ColumnDefinition, caret.Keywords);
    }

    /// <summary>
    /// 資料行定義的起點列的是資料表層級的條件約束，不借用 ADD 之後那一組。
    /// </summary>
    /// <remarks>
    /// <c>ALTER TABLE t ADD DEFAULT 0 FOR a</c> 合法，<c>CREATE TABLE t (DEFAULT …</c>
    /// 不合法——後者借用 <see cref="SqlKeywordPosition.AlterTableAdd"/> 時 DEFAULT 照樣列出來。
    /// 資料行層級的 NOT NULL、IDENTITY 要等型別寫完才接得上。
    /// </remarks>
    [Theory]
    [InlineData("CREATE TABLE t (", "CONSTRAINT", true)]
    [InlineData("CREATE TABLE t (", "PRIMARY", true)]
    [InlineData("CREATE TABLE t (", "UNIQUE", true)]
    [InlineData("CREATE TABLE t (", "INDEX", true)]
    [InlineData("CREATE TABLE t (", "CHECK", true)]
    [InlineData("CREATE TABLE t (a INT, ", "FOREIGN", true)]
    [InlineData("DECLARE @t TABLE (", "PRIMARY", true)]
    [InlineData("CREATE TABLE t (", "DEFAULT", false)]
    [InlineData("CREATE TABLE t (", "NOT", false)]
    [InlineData("CREATE TABLE t (", "IDENTITY", false)]
    [InlineData("CREATE TABLE t (", "SELECT", false)]

    // 非保留字在這裡只是被當成資料行名稱吃下去，不算屬於這個位置。
    [InlineData("CREATE TABLE t (", "NOLOCK", false)]
    [InlineData("CREATE TABLE t (", "OUTPUT", false)]
    [InlineData("CREATE TABLE t (", "APPLY", false)]
    [InlineData("ALTER TABLE t ADD ", "DEFAULT", true)]
    public void 資料行定義的起點只列條件約束關鍵字(string textBeforeToken, string keyword, bool expected)
    {
        Assert.Equal(expected, AllowedAt(textBeforeToken, keyword));
    }

    /// <summary>
    /// 變數是一個算完的運算元，位置由前面的子句決定。
    /// </summary>
    /// <remarks>
    /// 正在打的 <c>@名稱</c> 不會走到這裡：位置分析拿到的是「不含正在輸入的那個詞元」
    /// 的文字，而詞元起點落在小老鼠上。宣告與引用的分辨在上下文分析，
    /// 見 <c>SqlScriptVariableTests</c>。
    /// </remarks>
    [Theory]
    [InlineData("SELECT @x ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t WHERE a = @x ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    public void 變數之後由子句決定位置(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 逗號代表清單再來一項，位置回到清單的起點。
    /// </summary>
    /// <remarks>
    /// 判成尾端的話 <c>SELECT a, </c> 列的是 FROM、INTO、ORDER 這些接在整份選取清單
    /// 之後的字，而 CASE、CONVERT 這些真的能寫在那裡的反而不見。
    ///
    /// 沒關上的左括號也是清單的起點，逗號之後與它之後同一格，不穿出去借外層子句：
    /// 借到 INTO 的資料來源或 SET 的指派目標時列不出 NULL。
    /// </remarks>
    [Theory]
    [InlineData("SELECT a, ", SqlKeywordPosition.SelectList)]
    [InlineData("SELECT * FROM t1, ", SqlKeywordPosition.DataSource)]
    [InlineData("SELECT * FROM t ORDER BY a, ", SqlKeywordPosition.OrderByColumn)]
    [InlineData("SELECT * FROM t WHERE a IN (1, ", SqlKeywordPosition.Any)]
    [InlineData("SELECT COUNT(a, ", SqlKeywordPosition.Any)]
    [InlineData("INSERT INTO dbo.Loan (CopyNo, ReaderId) VALUES (1, ", SqlKeywordPosition.Any)]
    [InlineData("SET @a = IIF(@a IS NULL, ", SqlKeywordPosition.Any)]
    [InlineData("SELECT * FROM dbo.fn_LoansByCopy(1, ", SqlKeywordPosition.Any)]
    [InlineData("SELECT * FROM t WHERE a IN (SELECT b, ", SqlKeywordPosition.SelectList)]
    public void 逗號回到清單起點(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// ORDER BY／GROUP BY 要的那個欄位，以及 ALTER TABLE 的三個位置。
    /// </summary>
    /// <remarks>
    /// 這四處以前一律回 <see cref="SqlKeywordPosition.Any"/>，於是 191 個關鍵字與
    /// 49 筆片段全部進場：同一組候選、同一個前綴 <c>C</c>，<c>SELECT C</c> 只有
    /// 62 筆而 <c>ORDER BY C</c> 有 118 筆，前 13 名全被捷徑以 <c>C</c> 開頭的片段
    /// 占滿。<c>Any</c> 是給「判不出來」用的，而分析器在這四處都判得出來。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM t ORDER BY ", SqlKeywordPosition.OrderByColumn)]
    [InlineData("SELECT * FROM t GROUP BY ", SqlKeywordPosition.OrderByColumn)]

    // 欄位之後仍然是 ASC／DESC，不是另一個欄位。
    [InlineData("SELECT * FROM t ORDER BY a ", SqlKeywordPosition.OrderByTail | SqlKeywordPosition.OperandTail)]

    [InlineData("ALTER TABLE t ", SqlKeywordPosition.AlterTableAction)]
    [InlineData("ALTER TABLE dbo.t ", SqlKeywordPosition.AlterTableAction)]
    [InlineData("ALTER TABLE dbo.t ADD ", SqlKeywordPosition.AlterTableAdd)]
    [InlineData("ALTER TABLE dbo.t ALTER COLUMN ", SqlKeywordPosition.AlterTableColumn)]
    [InlineData("ALTER TABLE dbo.t DROP COLUMN ", SqlKeywordPosition.AlterTableColumn)]

    // 認的是「往回正好是 ALTER TABLE 加一個名稱單位」，不是「這份指令碼裡有沒有
    // ALTER TABLE」：接在後面的獨立敘述不屬於它。
    [InlineData("ALTER TABLE dbo.t ADD a INT;\nSELECT ", SqlKeywordPosition.SelectList)]
    [InlineData("CREATE TABLE dbo.t ", SqlKeywordPosition.Any)]
    public void 欄位與ALTER_TABLE的位置不再fail_open(
        string textBeforeToken,
        SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    [Fact]
    public void 加引號的識別字不當成關鍵字()
    {
        // FROM [FROM] 裡的 [FROM] 是資料表名稱，游標在它後面是資料來源之後、
        // 不是 FROM 之後。當成關鍵字的話這裡會是 DataSource。
        // 換行寫是為了避開別名的位置，那是另一條規則；換行順帶帶進來的
        // StatementStart 是第三條，兩者都與這裡要守的事無關。
        Assert.Equal(
            SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.StatementStart,
            SqlKeywordPositionAnalyzer.Analyze("SELECT * FROM [FROM]\r\n").Keywords);
    }

    [Theory]
    [InlineData("SELECT * FROM t ORDER BY a ", "DESC", true)]
    [InlineData("", "DESC", false)]
    [InlineData("SELECT * FROM t WHERE ", "DESC", false)]
    [InlineData("", "SELECT", true)]
    [InlineData("", "USE", true)]
    [InlineData("", "RESTORE", true)]
    [InlineData("SELECT * FROM t WHERE ", "PROCEDURE", false)]
    [InlineData("CREATE ", "PROCEDURE", true)]

    // JOIN 條件寫完之後同時是述詞的尾端與資料來源的尾端：AND 與 WHERE 都要在。
    [InlineData("SELECT * FROM a JOIN b ON b.x = a.x ", "WHERE", true)]
    [InlineData("SELECT * FROM a JOIN b ON b.x = a.x ", "AND", true)]
    [InlineData("SELECT * FROM a JOIN b ON b.x = a.x ", "INNER", true)]

    // 衍生資料表寫完、補上別名之後才輪到子句關鍵字。
    [InlineData("SELECT * FROM (SELECT 1 AS a) d ", "WHERE", true)]

    // SET 子句寫完之後接得了 WHERE、FROM、OPTION，而那一整組字掛的是資料來源尾端。
    // 只給述詞尾端的症狀是 UPDATE 寫到一半打不出 WHERE，而那是這個語句最常打的
    // 下一個字；暫存資料表與資料表變數走的是同一條路，一起守。
    [InlineData("UPDATE dbo.Loan SET CopyNo = 'C1' ", "WHERE", true)]
    [InlineData("UPDATE #Loan\r\nSET CopyNo = 'C1'\r\n", "WHERE", true)]
    [InlineData("UPDATE @Loan\r\nSET CopyNo = 'C1'\r\n", "WHERE", true)]

    // 述詞續寫的字不能因此掉：位置是聯集，不是換一個。
    [InlineData("UPDATE dbo.Loan SET CopyNo = 'C1' ", "AND", true)]

    // 選取清單的下一項要的是運算式，不是接在整份清單之後的字。
    [InlineData("SELECT a, ", "CASE", true)]
    [InlineData("SELECT a, ", "FROM", false)]

    // ORDER BY 的欄位位置：運算式關鍵字要在，語句級的字不能在。
    [InlineData("SELECT * FROM t ORDER BY ", "CASE", true)]
    [InlineData("SELECT * FROM t ORDER BY ", "CONVERT", true)]
    [InlineData("SELECT * FROM t ORDER BY ", "CREATE", false)]
    [InlineData("SELECT * FROM t ORDER BY ", "PROCEDURE", false)]

    // DESC 屬於欄位「之後」，在欄位這一格不該出現。
    [InlineData("SELECT * FROM t ORDER BY ", "DESC", false)]

    // GROUP BY 與 ORDER BY 的欄位之後各接各的字。
    [InlineData("SELECT a FROM t GROUP BY a ", "HAVING", true)]
    [InlineData("SELECT a FROM t GROUP BY a ", "ORDER", true)]
    [InlineData("SELECT a FROM t GROUP BY a ", "DESC", false)]
    [InlineData("SELECT a FROM t ORDER BY a ", "HAVING", false)]

    // DELETE 的目標之後是 WHERE。
    [InlineData("DELETE FROM dbo.Loan ", "WHERE", true)]

    // ALTER TABLE：成熟的補全工具在 ADD 之後給的就是這幾個字。
    [InlineData("ALTER TABLE dbo.t ", "ADD", true)]
    [InlineData("ALTER TABLE dbo.t ", "ALTER", true)]
    [InlineData("ALTER TABLE dbo.t ", "SELECT", false)]
    [InlineData("ALTER TABLE dbo.t ADD ", "CONSTRAINT", true)]
    [InlineData("ALTER TABLE dbo.t ADD ", "DEFAULT", true)]
    [InlineData("ALTER TABLE dbo.t ADD ", "PRIMARY", true)]
    [InlineData("ALTER TABLE dbo.t ADD ", "UNIQUE", true)]
    [InlineData("ALTER TABLE dbo.t ADD ", "CREATE", false)]
    [InlineData("ALTER TABLE dbo.t ADD ", "PROCEDURE", false)]

    // 區塊寫完：下一句的字，加上只接在 END 後面的 ELSE、TRY、CATCH。
    [InlineData("IF @a = 1 BEGIN SELECT 1 END ", "ELSE", true)]
    [InlineData("IF @a = 1 BEGIN SELECT 1 END ", "SELECT", true)]
    [InlineData("IF @a = 1 BEGIN SELECT 1 END ", "WHERE", false)]
    [InlineData("BEGIN TRY SELECT 1 END ", "TRY", true)]
    [InlineData("BEGIN TRY SELECT 1 END TRY BEGIN CATCH SELECT 1 END ", "CATCH", true)]
    [InlineData("", "ELSE", false)]

    // IF 條件寫完：主體那一句的開頭，或條件續寫的 AND。
    [InlineData("IF @a = 1 ", "RETURN", true)]
    [InlineData("IF @a = 1 ", "AND", true)]
    [InlineData("IF @a = 1 ", "PROCEDURE", false)]

    // 游標選項之後只有 FOR；DESC 之後不再是整份目錄。
    [InlineData("DECLARE c CURSOR LOCAL ", "FOR", true)]
    [InlineData("DECLARE c CURSOR LOCAL ", "SELECT", false)]
    [InlineData("SELECT a FROM t ORDER BY a DESC ", "OFFSET", true)]
    [InlineData("SELECT a FROM t ORDER BY a DESC ", "HAVING", false)]
    public void 位置過濾決定關鍵字出不出現(string textBeforeCaret, string keyword, bool expected)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret + keyword.Substring(0, 1));

        // 與執行期相同：比對到片語時，那一格的關鍵字來自片語。
        var suggestions = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty)
            .Concat(context.ClausePhrase?.Suggestions ?? Enumerable.Empty<SqlSuggestion>());

        var matched = SuggestionContextFilter
            .Filter(suggestions, context)
            .Any(suggestion =>
                suggestion.Kind == SuggestionKind.Keyword &&
                suggestion.DisplayText == keyword);

        Assert.Equal(expected, matched);
    }

    /// <summary>
    /// 位置過濾也管資料庫物件。
    /// </summary>
    /// <remarks>
    /// 名稱沒有位置旗標可帶——它們是執行期從中繼資料來的，所以反過來列
    /// 「哪些位置一個名稱都不接受」。少了這一半的症狀是 <c>ALTER TABLE t |</c>
    /// 之後照樣列出整個資料庫的資料表與預存程序，而文法上對的只有八個字。
    ///
    /// 兩個方向都要守。判不出位置時回傳的 <c>Any</c> 含著那份清單裡的每一個旗標，
    /// 用位元交集判斷的話 fail-open 會變成 fail-closed，<b>每一個</b>位置的資料庫
    /// 物件都會消失——那比原本的雜訊嚴重得多。
    /// </remarks>
    [Theory]
    [InlineData("ALTER TABLE dbo.t ", false)]
    [InlineData("ALTER TABLE dbo.t ADD ", false)]
    [InlineData("CREATE ", false)]
    [InlineData("SELECT * FROM t ORDER ", false)]

    // 選項值只有 ON、OFF 這些字。要資料表的 IDENTITY_INSERT 不是選項值的位置。
    [InlineData("SET NOCOUNT ", false)]
    [InlineData("SET IDENTITY_INSERT dbo.Loan ", false)]
    [InlineData("SET IDENTITY_INSERT ", true)]

    // 反方向：這些位置本來就是要選名稱的，一個都不能少。
    [InlineData("SELECT * FROM ", true)]
    [InlineData("SELECT ", true)]
    [InlineData("SELECT * FROM t WHERE ", true)]
    [InlineData("SELECT * FROM t ORDER BY ", true)]

    // INSERT 之後的 INTO 可以省略，所以那裡的資料表要留著。
    [InlineData("INSERT ", true)]

    // 資料行定義的起點是新名字或條件約束，型別之後是選項，沒有既有物件是對的。
    [InlineData("CREATE TABLE t (", false)]
    [InlineData("CREATE TABLE t (a int, ", false)]
    [InlineData("CREATE TABLE t (a int ", false)]

    // 判不出位置時是 Any，那是 fail-open：名稱照列。
    [InlineData("SELECT * FROM t WHERE a = 1 AND ", true)]
    [InlineData("CREATE TABLE t (a int DEFAULT ", true)]

    // 子句尾端：一項剛寫完，同一行只接運算子或關鍵字；別名是新名字，不是既有物件。
    [InlineData("SELECT * FROM t GROUP BY a ", false)]
    [InlineData("SELECT * FROM t ORDER BY a ", false)]
    [InlineData("SELECT * FROM t WHERE a = 1 ", false)]
    [InlineData("SELECT * FROM t a ", false)]
    [InlineData("SELECT * FROM t ", false)]
    [InlineData("SELECT a ", false)]
    [InlineData("UPDATE t SET a = 1 ", false)]

    // 換行後同時是下一句的開頭，那裡也寫不出資料表或欄位。
    [InlineData("SELECT * FROM t a\n", false)]
    [InlineData("SELECT 1; ", false)]
    [InlineData("BEGIN ", false)]
    [InlineData("", false)]

    // 條件、區塊與游標選項之後同樣寫不出既有物件；主體那一句判不出來時照列。
    [InlineData("IF @a = 1 ", false)]
    [InlineData("IF @a = 1 BEGIN SELECT 1 END ", false)]
    [InlineData("DECLARE c CURSOR LOCAL ", false)]
    [InlineData("CREATE TRIGGER tr ON t ", false)]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN ", false)]
    [InlineData("BACKUP DATABASE d TO DISK = 'x' WITH ", false)]
    [InlineData("IF @a = 1 PRINT 'x' ", true)]

    // 權限的 ON 之後可以直接寫物件；PIVOT 的 FOR 之前與 TABLESAMPLE 的大小之後寫不出名稱。
    [InlineData("GRANT SELECT ON ", true)]
    [InlineData("SELECT * FROM t PIVOT (SUM(x) ", false)]
    [InlineData("SELECT * FROM t TABLESAMPLE (10 ", false)]
    public void 位置過濾也管資料庫物件(string textBeforeCaret, bool expected)
    {
        var table = new SqlSuggestion(
            "Lib_Reader",
            "[dbo].[Lib_Reader]",
            "Table · dbo",
            "Table Lib_Reader",
            SuggestionKind.Table,
            schemaName: "dbo");

        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret + "L");

        Assert.Equal(expected, SuggestionContextFilter.Filter(new[] { table }, context).Count == 1);
    }

    /// <summary>
    /// 省略 EXEC 的程序呼叫只在批次第一句合法。
    /// </summary>
    /// <remarks>
    /// 語句開頭本身不接名稱；放行成每一句的開頭的話，<c>FROM t a⏎CREA</c> 會列出
    /// 所有名稱含 Create 的預存程序，而那裡寫上去就是語法錯誤。
    /// </remarks>
    [Theory]
    [InlineData("", true)]
    [InlineData("SELECT 1\nGO\n", true)]
    [InlineData("EXEC ", true)]
    [InlineData("SELECT 1; ", false)]
    [InlineData("SELECT * FROM t a\n", false)]
    [InlineData("SELECT * FROM t WHERE a = 1 ", false)]
    public void 批次第一句才列出不加EXEC的程序(string textBeforeCaret, bool expected)
    {
        var procedure = new SqlSuggestion(
            "usp_Loan",
            "[dbo].[usp_Loan]",
            "Procedure · dbo",
            "Procedure usp_Loan",
            SuggestionKind.Procedure,
            schemaName: "dbo");

        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret + "u");

        Assert.Equal(expected, SuggestionContextFilter.Filter(new[] { procedure }, context).Count == 1);
    }

    public static TheoryData<SqlKeywordPosition, string> GeneratorTemplates()
    {
        var data = new TheoryData<SqlKeywordPosition, string>();

        foreach (var template in SqlKeywordCatalogData.Templates)
        {
            data.Add(template.Key, template.Value);
        }

        return data;
    }

    /// <summary>
    /// 產生器的每一個樣板都是分析器判得出、而且回報含該位置的文字。
    /// </summary>
    /// <remarks>
    /// 樣板與分析器說的必須是同一個位置。只為了讓某個字脫離 None 而塞進別的位置的樣板，
    /// 會讓那個字出現在錯的地方——<c>SET NOCOUNT </c> 掛在資料來源尾端時，OFF 出現在每一個
    /// <c>FROM t </c> 之後。回報 <see cref="SqlKeywordPosition.Any"/> 不算：那代表分析器
    /// 根本不認得那段文字。
    /// </remarks>
    [Theory]
    [MemberData(nameof(GeneratorTemplates))]
    public void 產生器的樣板與分析器說的是同一個位置(SqlKeywordPosition position, string template)
    {
        var keywords = SqlKeywordPositionAnalyzer.Analyze(template).Keywords;

        Assert.NotEqual(SqlKeywordPosition.Any, keywords);
        Assert.NotEqual(SqlKeywordPosition.None, keywords & position);
    }

    /// <summary>
    /// <c>AS</c> 只在一項剛寫完、還沒有別名時才是別名。
    /// </summary>
    /// <remarks>
    /// 與同一行沒有 AS 的別名是同一條規則。<c>FOR SYSTEM_TIME AS </c> 也落在資料來源尾端，
    /// 只看位置的話會被當成別名，清單整個不開，<c>OF</c> 打不出來。
    /// </remarks>
    [Theory]
    [InlineData("SELECT a FROM t FOR SYSTEM_TIME AS ", false)]
    [InlineData("EXECUTE AS ", false)]
    [InlineData("CREATE PROC p WITH EXECUTE AS ", false)]
    [InlineData("CREATE VIEW v AS ", false)]
    [InlineData("CREATE OR ALTER VIEW v AS ", false)]
    [InlineData("CREATE PROC p AS ", false)]
    [InlineData("CREATE PROCEDURE p @a INT AS ", false)]
    [InlineData("CREATE TYPE dbo.T AS ", false)]
    [InlineData("CREATE TYPE dbo.T AS TABLE (", false)]
    [InlineData("CREATE FUNCTION f() RETURNS INT AS ", false)]
    [InlineData("WITH c AS ", false)]
    [InlineData("SELECT CAST(a AS ", false)]
    [InlineData("SELECT a AS ", true)]
    [InlineData("SELECT a + b AS ", true)]
    [InlineData("SELECT CASE WHEN a = 1 THEN 2 END AS ", true)]
    [InlineData("SELECT * FROM t AS ", true)]
    [InlineData("SELECT * FROM dbo.fn(1) AS ", true)]
    [InlineData("SELECT * FROM (SELECT 1 a) AS ", true)]
    [InlineData("MERGE INTO t AS ", true)]
    [InlineData("MERGE INTO t AS x USING s AS ", true)]
    public void AS只在一項剛寫完時是別名(string textBeforeToken, bool expected)
    {
        Assert.Equal(
            expected,
            SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Slot == SqlCompletionSlot.Name);
    }

    [Fact]
    public void FOR_SYSTEM_TIME_AS之後列得出OF()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("SELECT a FROM t FOR SYSTEM_TIME AS O");
        var suggestions = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(context.ClausePhrase?.Suggestions ?? Enumerable.Empty<SqlSuggestion>());

        Assert.Equal(SqlCompletionSlot.Grammar, context.Slot);
        Assert.Contains(SuggestionContextFilter.Filter(suggestions, context), suggestion => suggestion.DisplayText == "OF");
    }

    /// <summary>
    /// SET 之後只有選項名稱時是選項值的位置；UPDATE（含 MERGE 的 UPDATE）的 SET 子句不是。
    /// </summary>
    /// <remarks>
    /// 名稱停在關鍵字上時還沒寫完：<c>SET IDENTITY_INSERT </c> 之後要資料表，
    /// <c>SET TRANSACTION </c> 之後是 ISOLATION，那裡判不出位置。
    /// </remarks>
    [Theory]
    [InlineData("SET NOCOUNT ", SqlKeywordPosition.SetOptionValue)]
    [InlineData("BEGIN SET NOCOUNT ", SqlKeywordPosition.SetOptionValue)]
    [InlineData(";SET XACT_ABORT ", SqlKeywordPosition.SetOptionValue)]
    [InlineData("SET NOCOUNT ON SET XACT_ABORT ", SqlKeywordPosition.SetOptionValue)]
    [InlineData("UPDATE t SET a = 1 SET NOCOUNT ", SqlKeywordPosition.SetOptionValue)]
    [InlineData("DECLARE @n INT SET NOCOUNT ", SqlKeywordPosition.SetOptionValue)]
    [InlineData("ALTER PROCEDURE p AS SET NOCOUNT ", SqlKeywordPosition.SetOptionValue)]
    [InlineData("CREATE TRIGGER tr ON t AFTER UPDATE AS SET NOCOUNT ", SqlKeywordPosition.SetOptionValue)]
    [InlineData("IF UPDATE(a) SET NOCOUNT ", SqlKeywordPosition.SetOptionValue)]
    [InlineData("SET IDENTITY_INSERT dbo.Loan ", SqlKeywordPosition.SetOptionValue)]
    [InlineData("SET TRANSACTION ISOLATION LEVEL ", SqlKeywordPosition.SetOptionValue)]
    [InlineData("SET ANSI_NULLS, QUOTED_IDENTIFIER ", SqlKeywordPosition.SetOptionValue)]
    [InlineData("SET STATISTICS IO ", SqlKeywordPosition.SetOptionValue)]
    [InlineData("SET IDENTITY_INSERT ", SqlKeywordPosition.Any)]
    [InlineData("SET TRANSACTION ", SqlKeywordPosition.Any)]
    [InlineData("UPDATE t SET a ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.UpdateSetTail | SqlKeywordPosition.OperandTail)]
    [InlineData("UPDATE TOP (5) t WITH (TABLOCK) SET a ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.UpdateSetTail | SqlKeywordPosition.OperandTail)]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN MATCHED THEN UPDATE SET a ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.MergeClause | SqlKeywordPosition.OperandTail)]
    [InlineData("ALTER DATABASE CURRENT SET RECOVERY ", SqlKeywordPosition.SetOptionValue)]
    [InlineData("UPDATE t SET a = 1 ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.UpdateSetTail | SqlKeywordPosition.OperandTail)]
    [InlineData("UPDATE t SET a = 1, ", SqlKeywordPosition.SetTarget)]
    [InlineData("UPDATE t SET a = 1, b ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.UpdateSetTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SET NOCOUNT ON SELECT a FROM t ", SqlKeywordPosition.TableSourceTail)]
    public void SET之後的選項名稱(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// DML 的 OUTPUT 清單是一份選取清單：一項寫完接 AS、INTO 與那句 DML 本來接得了的字，逗號之後是運算式。
    /// </summary>
    /// <remarks>
    /// 只認子句錨點時 UPDATE 的 OUTPUT 借到 SET：一項寫完沒有 AS、INTO，逗號之後還當成指派。
    /// 參數的 OUTPUT（<c>@a int OUTPUT</c>、<c>EXEC p @x = @y OUTPUT</c>）不是。
    /// </remarks>
    [Theory]
    [InlineData("UPDATE t SET a = 1 OUTPUT inserted.a ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.UpdateSetTail | SqlKeywordPosition.OperandTail)]
    [InlineData("UPDATE t SET a = 1 OUTPUT inserted.a, ", SqlKeywordPosition.SelectList)]
    [InlineData("UPDATE t SET a = 1 OUTPUT inserted.a INTO @t ", SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.UpdateSetTail | SqlKeywordPosition.OperandTail)]
    [InlineData("INSERT INTO t (a) OUTPUT inserted.a ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.OperandTail)]
    [InlineData("UPDATE t SET a = 1 OUTPUT inserted.a FROM s ", SqlKeywordPosition.TableSourceTail)]
    [InlineData("EXEC p @x = @y OUTPUT, ", SqlKeywordPosition.Any)]
    public void DML的OUTPUT清單(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// Service Broker 的 RECEIVE 清單也是一份選取清單，在 <c>WAITFOR (</c> 裡也一樣。
    /// </summary>
    [Theory]
    [InlineData("RECEIVE ", SqlKeywordPosition.SelectList)]
    [InlineData("RECEIVE TOP (1) message_body, ", SqlKeywordPosition.SelectList)]
    [InlineData("WAITFOR (RECEIVE message_body, ", SqlKeywordPosition.SelectList)]
    [InlineData("WAITFOR (RECEIVE message_body ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT 1\nRECEIVE message_body ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    public void RECEIVE清單(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// SET 選項的值寫完，這一句就結束了。
    /// </summary>
    /// <remarks>
    /// SSMS 產生的指令碼開頭是 <c>SET ANSI_NULLS ON⏎GO</c>：ON 被當成 JOIN 的 ON 時，
    /// 下一行打 <c>G</c> 列的是 GROUPING，Enter 把 GO 換掉。SET 選項沒有可以續寫的子句，
    /// 所以同一行也是下一句的開頭。
    ///
    /// 識別字的值與名稱的延續分不開（<c>SET DATEFORMAT dmy</c> 與 <c>SET IDENTITY_INSERT t</c>），
    /// 那種值只在換行之後補上語句開頭。
    /// </remarks>
    [Theory]
    [InlineData("SET NOCOUNT ON ", SqlKeywordPosition.StatementStart)]
    [InlineData("SET ANSI_NULLS ON\n", SqlKeywordPosition.StatementStart)]
    [InlineData("SET NOCOUNT OFF ", SqlKeywordPosition.StatementStart)]
    [InlineData("SET STATISTICS IO ON ", SqlKeywordPosition.StatementStart)]
    [InlineData("SET IDENTITY_INSERT dbo.Loan ON ", SqlKeywordPosition.StatementStart)]
    [InlineData("SET TRANSACTION ISOLATION LEVEL READ COMMITTED ", SqlKeywordPosition.StatementStart)]
    [InlineData("SET ROWCOUNT 10 ", SqlKeywordPosition.StatementStart)]
    [InlineData("SET LOCK_TIMEOUT -1 ", SqlKeywordPosition.StatementStart)]
    [InlineData("SET ANSI_NULLS, QUOTED_IDENTIFIER ON ", SqlKeywordPosition.StatementStart)]
    [InlineData("IF UPDATE(a) SET NOCOUNT ON ", SqlKeywordPosition.StatementStart)]
    [InlineData("ALTER DATABASE CURRENT SET ANSI_NULLS ON ", SqlKeywordPosition.StatementStart)]
    [InlineData("SET DATEFORMAT dmy\n", SqlKeywordPosition.SetOptionValue | SqlKeywordPosition.StatementStart)]
    [InlineData("ALTER DATABASE CURRENT SET RECOVERY SIMPLE\n", SqlKeywordPosition.SetOptionValue | SqlKeywordPosition.StatementStart)]
    [InlineData("SET DATEFORMAT dmy ", SqlKeywordPosition.SetOptionValue)]
    public void SET選項的值寫完就是一句的結尾(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    [Theory]
    [InlineData("SET ANSI_NULLS ON\n", "GO")]
    [InlineData("SET QUOTED_IDENTIFIER ON\r\n", "GO")]
    [InlineData("SET NOCOUNT ON\n", "SELECT")]
    [InlineData("SET NOCOUNT ON ", "SELECT")]
    [InlineData("SET STATISTICS IO ON\n", "SELECT")]
    public void SET選項之後列得出下一句的字(string textBeforeToken, string keyword)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeToken + keyword);

        // SET STATISTICS IO ON 的 ON 不是 CREATE STATISTICS s ON 的 ON，後面不是資料表。
        Assert.Equal(CompletionTarget.Any, context.Target);

        // 打完整個字時排第一：Enter 提交的就是它，不會被換成 GROUPING。
        var ranked = SuggestionListProbe.Match(BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current), context);

        Assert.Equal(keyword, ranked.First().DisplayText);
    }

    /// <summary>
    /// 產生器判不出位置的字只在分析器也判不出位置時出現。
    /// </summary>
    /// <remarks>
    /// 以前它們在讀進來時就換成 <see cref="SqlKeywordPosition.Any"/>，
    /// <c>SELECT C</c> 的清單裡因此有 <c>CURSOR</c>、<c>WHERE N</c> 有 <c>NOLOCK</c>。
    /// 整個藏起來也不行：判不出位置的地方正是這些字真正的用處。
    /// 走的是產品的過濾路徑，不只比旗標。
    /// </remarks>
    [Theory]
    [InlineData("SELECT C", "CURSOR", false)]
    [InlineData("SELECT * FROM t WHERE N", "NOLOCK", false)]
    [InlineData("P", "PLAN", false)]
    [InlineData("SELECT * FROM t C", "CASCADE", false)]
    [InlineData("UPDATE t SET S", "STOPLIST", false)]
    [InlineData("CREATE TABLE t (N", "NOLOCK", false)]
    [InlineData("SELECT * FROM t CROSS A", "APPLY", true)]
    [InlineData("DECLARE c C", "CURSOR", true)]
    [InlineData("CREATE FULLTEXT INDEX ON t (a) KEY INDEX pk ON c WITH S", "STOPLIST", true)]
    [InlineData("SELECT * FROM t WHERE a = A", "ANY", true)]
    public void 判不出位置的關鍵字只在判不出位置時出現(string textBeforeCaret, string keyword, bool expected)
    {
        Assert.Equal(SqlKeywordPosition.None, SqlKeywordCatalog.GetPositions(keyword));

        var suggestions = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current);
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.Equal(
            expected,
            SuggestionContextFilter.Filter(suggestions, context).Any(suggestion => suggestion.DisplayText == keyword));
    }

    /// <summary>
    /// 補了樣板的字出現在它真正的位置，不再靠「判不出位置一律放行」。
    /// </summary>
    [Theory]
    [InlineData("SELECT * FROM t WHERE a LIKE 'x' ", "ESCAPE", true)]
    [InlineData("SELECT TOP 10 ", "PERCENT", true)]
    [InlineData("SELECT TOP (10) ", "PERCENT", true)]
    [InlineData("SELECT TOP 10 PERCENT ", "WITH", true)]
    [InlineData("SELECT ", "PERCENT", false)]
    [InlineData("SELECT ", "WITH", false)]
    [InlineData("SET NOCOUNT ", "OFF", true)]
    [InlineData("SET IDENTITY_INSERT dbo.Loan ", "OFF", true)]
    [InlineData("SELECT * FROM t ", "OFF", false)]
    [InlineData("SELECT * FROM t ", "READ", false)]
    [InlineData("DELETE FROM t WHERE ", "CURRENT", true)]
    [InlineData("BEGIN ", "DISTRIBUTED", true)]

    // 非保留字：以關鍵字的身分屬於的位置。
    [InlineData("BEGIN ", "TRY", true)]
    [InlineData("BEGIN TRY SELECT 1 END TRY BEGIN ", "CATCH", true)]
    [InlineData("SELECT 1; ", "THROW", true)]
    [InlineData("SELECT ", "NEXT", true)]
    [InlineData("SELECT * FROM t ORDER BY a ", "OFFSET", true)]
    [InlineData("SELECT * FROM t ORDER BY a OFFSET 10 ", "ROWS", true)]
    [InlineData("MERGE INTO t ", "USING", true)]
    [InlineData("INSERT INTO t ", "OUTPUT", true)]
    [InlineData("SELECT ", "THROW", false)]
    [InlineData("SELECT * FROM t WHERE ", "OFFSET", false)]
    public void 補了樣板的字出現在它的位置(string textBeforeToken, string keyword, bool expected)
    {
        Assert.Equal(expected, AllowedAt(textBeforeToken, keyword));
    }

    /// <summary>
    /// CASE 的裡面列 CASE 的字，不列外層子句的字。
    /// </summary>
    [Theory]
    [InlineData("SELECT CASE WHEN a ", "IN", true)]
    [InlineData("SELECT CASE WHEN a ", "IS", true)]
    [InlineData("SELECT CASE WHEN a ", "LIKE", true)]
    [InlineData("SELECT CASE WHEN a ", "BETWEEN", true)]
    [InlineData("SELECT CASE WHEN a ", "NOT", true)]
    [InlineData("SELECT CASE WHEN a = 1 ", "THEN", true)]
    [InlineData("SELECT CASE WHEN a = 1 ", "AND", true)]
    [InlineData("SELECT CASE WHEN a = 1 ", "OR", true)]
    [InlineData("SELECT CASE a WHEN 1 ", "THEN", true)]
    [InlineData("SELECT CASE WHEN a = 1 THEN 1 ", "ELSE", true)]
    [InlineData("SELECT CASE WHEN a = 1 THEN 1 ", "END", true)]
    [InlineData("SELECT CASE WHEN a = 1 THEN 1 ", "WHEN", true)]
    [InlineData("SELECT CASE WHEN a = 1 ", "FROM", false)]
    [InlineData("SELECT CASE WHEN a = 1 ", "WHERE", false)]
    [InlineData("SELECT CASE WHEN a = 1 ", "GROUP", false)]
    [InlineData("SELECT CASE WHEN a ", "UNION", false)]
    [InlineData("SELECT CASE WHEN a = 1 THEN 1 ", "THEN", false)]
    public void CASE裡面列CASE的字(string textBeforeToken, string keyword, bool expected)
    {
        Assert.Equal(expected, AllowedAt(textBeforeToken, keyword));
    }

    private static bool AllowedAt(string textBeforeToken, string keyword)
    {
        return SqlKeywordCatalog.GetPositions(keyword)
            .Allows(SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 換行之後的子句尾端也是下一句的開頭。
    /// </summary>
    /// <remarks>
    /// T-SQL 的分號是選用的，敘述的結尾沒有任何詞元標示得出來——
    /// <c>WHERE a = 1</c> 之後換行寫 <c>SELECT</c> 與換行寫 <c>AND</c>，
    /// 在詞元串流上完全一樣。少了這一條，使用者不打分號時下一句的語句級片段
    /// 一個都不會出現，而打了分號就有；他看不出兩者的差別，只會覺得片段時有時無。
    ///
    /// 補的是位元不是換一個，所以續寫子句的字一個都不能少——那是這個修正
    /// 從一個問題換成另一個問題的地方。
    /// </remarks>
    [Theory]
    [InlineData("UPDATE dbo.Loan SET CopyNo = 'C1' WHERE ReaderId = 1\r\n", "ssf", true)]
    [InlineData("SELECT * FROM dbo.Loan\r\n", "ssf", true)]
    [InlineData("SELECT * FROM dbo.Loan ORDER BY CopyNo\r\n", "ssf", true)]

    // 同一行代表他還在寫同一個子句，語句級片段不進場。
    [InlineData("SELECT * FROM dbo.Loan WHERE ReaderId = 1 ", "ssf", false)]

    // 沒有 FROM 的 SELECT 也能是完整敘述，不能為了減少候選而封死下一句片段。
    [InlineData("SELECT CopyNo\r\n", "ssf", true)]

    // 反方向：續寫的字沒有因為多了語句開頭就掉。
    [InlineData("SELECT * FROM dbo.Loan WHERE ReaderId = 1\r\n", "AND", true)]
    [InlineData("SELECT * FROM dbo.Loan\r\n", "WHERE", true)]
    [InlineData("SELECT * FROM dbo.Loan ORDER BY CopyNo\r\n", "DESC", true)]
    public void 換行之後的子句尾端也是下一句的開頭(
        string textBeforeCaret,
        string displayText,
        bool expected)
    {
        var suggestions = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current);
        var context = SqlCompletionContextAnalyzer.Analyze(
            textBeforeCaret + displayText.Substring(0, 1));

        var matched = SuggestionContextFilter
            .Filter(suggestions, context)
            .Any(suggestion => suggestion.DisplayText == displayText);

        Assert.Equal(expected, matched);
    }

    [Theory]
    [InlineData("SELECT dbo.fn_Fee('')\n")]
    [InlineData("SELECT dbo.fn_Fee('')\r\n")]
    [InlineData("SELECT dbo.fn_Fee('')\r")]
    [InlineData("SELECT dbo.fn_Fee('')\n\n    ")]
    [InlineData("SELECT [dbo].[fn_Fee]('')\n")]
    [InlineData("SELECT LibArchive.dbo.fn_Fee('')\n")]
    [InlineData("SELECT dbo.fn_Fee(COALESCE(NULL, ''))\n")]
    [InlineData("SELECT 1\n")]
    [InlineData("SELECT N''\n")]
    [InlineData("SELECT @CopyNo\n")]
    [InlineData("SELECT 1 + 2\n")]
    [InlineData("SELECT (1 + 2)\n")]
    [InlineData("SELECT (SELECT 1)\n")]
    [InlineData("SELECT dbo.fn_Fee('') AS Fine\n")]
    [InlineData("SELECT dbo.fn_Fee(''), 1\n")]
    [InlineData("SELECT dbo.fn_Fee('') -- 計算費用\n")]
    [InlineData("SELECT dbo.fn_Fee('')\n/* 計算費用 */ ")]
    [InlineData("SELECT dbo.fn_Fee('') /* 外層\n /* 內層 */ 結束 */ ")]
    public void 選取清單跨行保留續寫位置並開放所有語句片段(string textBeforeToken)
    {
        var tokens = SqlTokenizer.Tokenize(textBeforeToken);
        var expected = SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail | SqlKeywordPosition.StatementStart;
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(tokens, textBeforeToken).Keywords);

        var suggestions = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current);
        foreach (var prefix in new[] { "", "s", "ss", "ssf", "SSF", "st100", "ssc", "ii", "cp", "SELECT", "FROM" })
        {
            var context = SqlCompletionContextAnalyzer.Analyze(textBeforeToken + prefix);
            Assert.Equal(expected, context.KeywordPosition);
            if (prefix.Length == 0)
            {
                // 一般位置未輸入前綴時仍不自動開清單，不能用放寬觸發掩蓋位置漏判。
                Assert.False(SqlCompletionPolicy.Participates(context, triggerAfterCharacters: 1));
                continue;
            }

            Assert.True(SqlCompletionPolicy.Participates(context, triggerAfterCharacters: 1));
            var displayText = prefix is "s" or "ss" or "SSF" ? "ssf" : prefix;
            Assert.Contains(SuggestionContextFilter.Filter(suggestions, context),
                suggestion => suggestion.DisplayText == displayText);
            Assert.Contains(SuggestionListProbe.Match(suggestions, context),
                suggestion => suggestion.DisplayText == displayText);
        }
    }

    [Theory]
    [InlineData("SELECT dbo.fn_Fee('') ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT dbo.fn_Fee('') /* 同一行 */ ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT dbo.fn_Fee('\n') ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT [Copy\nNo] ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT\n", SqlKeywordPosition.SelectList)]
    [InlineData("SELECT 1,\n", SqlKeywordPosition.SelectList)]
    [InlineData("SELECT dbo.fn_Fee(1\n", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT COALESCE(dbo.fn_Fee(''), 1\n", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT (SELECT 1\n", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData(";WITH LoanFees AS (SELECT 1\n", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM (SELECT 1\n", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM dbo.Loan WHERE ReaderId IN (1\n", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT (SELECT CopyNo FROM dbo.Loan\n", SqlKeywordPosition.TableSourceTail)]
    [InlineData("SELECT (SELECT CopyNo FROM dbo.Loan ORDER BY CopyNo\n", SqlKeywordPosition.OrderByTail | SqlKeywordPosition.OperandTail)]
    public void 同行或括號未關閉不因換行新增語句開頭(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
        var suggestions = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current);
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeToken + "ssf");
        Assert.DoesNotContain(SuggestionContextFilter.Filter(suggestions, context),
            suggestion => suggestion.DisplayText == "ssf");
    }

    /// <summary>
    /// <c>NOT</c> 同時是述詞的開頭與運算子的一半。
    /// </summary>
    /// <remarks>
    /// <c>WHERE NOT </c> 之後開始一個述詞，<c>a.Big5Code NOT </c> 之後接的是
    /// <c>IN</c>／<c>LIKE</c>／<c>BETWEEN</c>，而那三個字掛的是
    /// <see cref="SqlKeywordPosition.ExpressionTail"/>。只給述詞起點的症狀是
    /// <c>NOT </c> 之後打 <c>i</c> 完全等不到 <c>IN</c>。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM t WHERE NOT ")]
    [InlineData("SELECT * FROM t WHERE a NOT ")]
    [InlineData("SELECT * FROM t WHERE a = 1 AND t.b NOT ")]
    public void NOT之後同時是述詞起點與運算式尾端(string textBeforeToken)
    {
        var position = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords;

        Assert.Equal(
            SqlKeywordPosition.Predicate | SqlKeywordPosition.ExpressionTail,
            position);
        Assert.Contains(
            SqlKeywordCatalog.All,
            keyword => keyword == "IN" &&
                (SqlKeywordCatalog.GetPositions(keyword) & position) != SqlKeywordPosition.None);
    }

    /// <summary>
    /// 尾端的點號屬於正在輸入的那個名稱，位置由整個名稱之前的東西決定。
    /// </summary>
    /// <remarks>
    /// 少了這一條，限定字之後一律是 <see cref="SqlKeywordPosition.Any"/>，
    /// 而那讓 <c>FROM a, LibArchive.</c> 在位置上與 <c>SELECT LibArchive.</c>
    /// 沒有差別——前者要的只有資料來源。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM dbo.", SqlKeywordPosition.DataSource)]
    [InlineData("SELECT * FROM a, LibArchive.dbo.", SqlKeywordPosition.DataSource)]
    [InlineData("SELECT * FROM t WHERE t.", SqlKeywordPosition.Predicate)]
    [InlineData("SELECT t.", SqlKeywordPosition.SelectList)]
    [InlineData("SELECT * FROM t ORDER BY t.", SqlKeywordPosition.OrderByColumn)]

    // 空段不是限定字，照舊落在「判不出來」那一支。小數點根本走不到這裡：
    // 詞法分析把 1. 掃成一個數值詞元，位置仍然由子句錨點決定。
    [InlineData("SELECT * FROM LibArchive..", SqlKeywordPosition.Any)]
    [InlineData("SELECT 1.", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    public void 尾端點號由名稱之前的位置決定(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    [Theory]
    [InlineData("SELECT dbo.fn_Fee('');\n")]
    [InlineData("SELECT dbo.fn_Fee('')\nGO\n")]
    [InlineData("SELECT * FROM dbo.Loan WHERE ReaderId = 1\n/* 接續 */ ")]
    [InlineData("SELECT * FROM dbo.Loan\n/* 接續 */ ")]
    [InlineData("SELECT * FROM dbo.Loan ORDER BY CopyNo\n/* 接續 */ ")]
    public void 明確邊界及既有子句跨註解仍提供片段(string textBeforeToken)
    {
        var suggestions = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current);
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeToken + "ssf");
        Assert.Contains(SuggestionContextFilter.Filter(suggestions, context),
            suggestion => suggestion.DisplayText == "ssf");
    }

    /// <summary>
    /// 本身就把那一項寫完的關鍵字，之後與識別字之後相同，由往回找到的子句決定。
    /// </summary>
    /// <remarks>
    /// 以前這些字一律回 <see cref="SqlKeywordPosition.Any"/>：<c>ORDER BY a DESC </c> 之後
    /// 整份目錄與片段全部進場，後面的 <c>FOR</c> 也只能當成「可能」的片語。
    /// </remarks>
    [Theory]
    [InlineData("SELECT a FROM t ORDER BY a DESC ", SqlKeywordPosition.OrderByTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT a FROM t ORDER BY a ASC, b DESC ", SqlKeywordPosition.OrderByTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT a FROM t ORDER BY a OFFSET 10 ROWS ", SqlKeywordPosition.OrderByTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT a FROM t WHERE a IS NULL ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT a FROM t WHERE a IS NOT NULL ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT a FROM t WHERE x = CURRENT_USER ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT CASE WHEN a IS NULL ", SqlKeywordPosition.CaseArm | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT NULL ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT CURRENT_TIMESTAMP ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t HOLDLOCK ", SqlKeywordPosition.TableSourceTail)]

    // TRAN 寫完的是語句本身，不是子句裡的一項：往回找只會找到上一句的 WHERE。
    [InlineData("SELECT a FROM t WHERE b = 1\nBEGIN TRAN ", SqlKeywordPosition.Any)]
    public void 寫完一項的關鍵字之後由子句決定位置(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 自成一項的關鍵字與常值一樣，後面同一行可能是別名。
    /// </summary>
    [Theory]
    [InlineData("SELECT NULL ", SqlCompletionSlot.MaybeName)]
    [InlineData("SELECT CURRENT_TIMESTAMP ", SqlCompletionSlot.MaybeName)]
    [InlineData("SELECT NULL x ", SqlCompletionSlot.Grammar)]
    public void 自成一項的關鍵字之後可能是別名(string textBeforeToken, SqlCompletionSlot expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Slot);
    }

    /// <summary>
    /// IF、WHILE 的條件寫完之後是主體那一句的開頭，也還能接 AND、OR。
    /// </summary>
    /// <remarks>
    /// 語句開頭只屬於條件那一層：括號還沒關上時仍在條件裡；已經寫了主體那一句的開頭，
    /// 而那一句沒有自己的子句關鍵字時判不出來，不拿 IF 的位置去猜。
    /// </remarks>
    [Theory]
    [InlineData("IF @a = 1 ", SqlKeywordPosition.StatementStart | SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("WHILE @i < 1 ", SqlKeywordPosition.StatementStart | SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("IF @a IS NULL ", SqlKeywordPosition.StatementStart | SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("IF EXISTS (SELECT 1 FROM t) ", SqlKeywordPosition.StatementStart | SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("IF NOT EXISTS (SELECT 1 FROM t) AND @a = 1 ",
        SqlKeywordPosition.StatementStart | SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT 1; WHILE @@FETCH_STATUS = 0 ",
        SqlKeywordPosition.StatementStart | SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("IF (@a = 1 ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("IF @a = 1 SELECT a FROM t WHERE b = 1 ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.IfBodyEnd | SqlKeywordPosition.OperandTail)]
    [InlineData("IF @a = 1 PRINT 'x' ", SqlKeywordPosition.Any)]
    [InlineData("IF @a = 1 EXEC dbo.p ", SqlKeywordPosition.Any)]
    public void IF與WHILE的條件寫完是主體的開頭(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 語句區塊的開頭與結尾之後是下一句；BEGIN … END 的 END 另外接得了 ELSE、TRY、CATCH。
    /// </summary>
    /// <remarks>
    /// CASE 的 ELSE 不是區塊邊界，照舊判不出來；寫完的 CASE … END 是運算元。
    /// </remarks>
    [Theory]
    [InlineData("IF @a = 1 SELECT 1 ELSE ", SqlKeywordPosition.StatementStart)]
    [InlineData("IF @a = 1 BEGIN SELECT 1 END ELSE ", SqlKeywordPosition.StatementStart)]
    [InlineData("IF CASE WHEN @a = 1 THEN 1 END = 1 PRINT 1 ELSE ", SqlKeywordPosition.StatementStart)]
    [InlineData("BEGIN TRY ", SqlKeywordPosition.StatementStart)]
    [InlineData("BEGIN TRY SELECT 1 END TRY ", SqlKeywordPosition.StatementStart)]
    [InlineData("BEGIN TRY SELECT 1 END TRY BEGIN CATCH ", SqlKeywordPosition.StatementStart)]
    [InlineData("BEGIN TRY SELECT 1 END TRY BEGIN CATCH SELECT 1 END CATCH ", SqlKeywordPosition.StatementStart)]
    [InlineData("BEGIN SELECT 1 END ", SqlKeywordPosition.BlockEnd | SqlKeywordPosition.StatementStart)]
    [InlineData("IF @a = 1 BEGIN SELECT 1 END ", SqlKeywordPosition.BlockEnd | SqlKeywordPosition.StatementStart)]
    [InlineData("BEGIN TRY SELECT 1 END ", SqlKeywordPosition.BlockEnd | SqlKeywordPosition.StatementStart)]
    [InlineData("SELECT CASE WHEN a = 1 THEN 1 ELSE ", SqlKeywordPosition.Any)]
    [InlineData("IF @a = 1 SELECT CASE WHEN b = 1 THEN 1 ELSE ", SqlKeywordPosition.Any)]
    [InlineData("SELECT CASE WHEN a = 1 THEN 1 END ", SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail)]
    public void 區塊邊界之後是下一句(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 敘述自己的格子：游標選項、觸發程序標頭、MERGE 的 WHEN、BACKUP／RESTORE 的選項清單。
    /// </summary>
    /// <remarks>
    /// 這些格子接的多半是非關鍵字，由只認位置的子句片語給；判不出位置時片語無從比對。
    /// 反方向也要守：CASE 的 WHEN、括號裡的逗號、BACKUP CERTIFICATE 的 WITH 不是這些格子。
    /// </remarks>
    [Theory]
    [InlineData("DECLARE c CURSOR ", SqlKeywordPosition.CursorOption)]
    [InlineData("DECLARE c CURSOR LOCAL FAST_FORWARD ", SqlKeywordPosition.CursorOption)]
    [InlineData("DECLARE [c] SCROLL CURSOR ", SqlKeywordPosition.CursorOption)]
    [InlineData("DECLARE @c CURSOR ", SqlKeywordPosition.Any)]
    [InlineData("DECLARE @n int, @c CURSOR ", SqlKeywordPosition.Any)]
    [InlineData("SET @c = CURSOR ", SqlKeywordPosition.CursorOption)]
    [InlineData("SET @c = CURSOR LOCAL SCROLL ", SqlKeywordPosition.CursorOption)]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan ", SqlKeywordPosition.TriggerHeader)]
    [InlineData("CREATE OR ALTER TRIGGER dbo.tr ON dbo.Loan WITH ENCRYPTION, EXECUTE AS 'u' ", SqlKeywordPosition.TriggerHeader)]
    [InlineData("ALTER TRIGGER tr ON Loan WITH EXECUTE AS CALLER ", SqlKeywordPosition.TriggerHeader)]
    [InlineData("CREATE TRIGGER tr ON ALL SERVER ", SqlKeywordPosition.TriggerHeader)]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan AFTER ", SqlKeywordPosition.TriggerEvent)]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan INSTEAD OF ", SqlKeywordPosition.TriggerEvent)]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan AFTER INSERT, UPDATE, ", SqlKeywordPosition.TriggerEvent)]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan WITH ENCRYPTION FOR ", SqlKeywordPosition.TriggerEvent)]
    [InlineData("ALTER TRIGGER tr ON dbo.Loan WITH EXECUTE AS CALLER AFTER ", SqlKeywordPosition.TriggerEvent)]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan AFTER INSERT, UPDATE ", SqlKeywordPosition.TriggerEventEnd)]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan INSTEAD OF DELETE ", SqlKeywordPosition.TriggerEventEnd)]
    [InlineData("CREATE TRIGGER tr ON DATABASE FOR CREATE_TABLE ", SqlKeywordPosition.TriggerEventEnd)]
    [InlineData("CREATE TRIGGER tr ON ALL SERVER FOR LOGON ", SqlKeywordPosition.TriggerEventEnd)]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan INSTEAD OF DELETE AS ", SqlKeywordPosition.StatementStart)]
    [InlineData("CREATE TRIGGER tr ON ALL SERVER FOR LOGON AS ", SqlKeywordPosition.StatementStart)]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN ", SqlKeywordPosition.MergeWhen)]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN MATCHED THEN UPDATE SET a = 1\nWHEN ", SqlKeywordPosition.MergeWhen)]
    [InlineData("MERGE t USING s ON t.a = s.a ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.MergeClause | SqlKeywordPosition.OperandTail)]
    [InlineData("MERGE dbo.Loan AS t USING (SELECT 1 AS a) AS s ON t.a = s.a ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.MergeClause | SqlKeywordPosition.OperandTail)]
    [InlineData("MERGE dbo.Loan AS t USING (SELECT @a, @b) AS s (a, b) ON (t.a = s.a) ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.MergeClause | SqlKeywordPosition.OperandTail)]
    [InlineData("MERGE dbo.Loan t USING (VALUES (1, 2)) s(a, b) ON t.a = s.a ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.MergeClause | SqlKeywordPosition.OperandTail)]
    [InlineData("MERGE dbo.Loan t USING dbo.fn_Copies(1) ON t.a = 1 ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.MergeClause | SqlKeywordPosition.OperandTail)]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN MATCHED AND t.b = 1 ", SqlKeywordPosition.CaseArm | SqlKeywordPosition.OperandTail)]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN MATCHED THEN ", SqlKeywordPosition.MergeAction)]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN MATCHED THEN DELETE ", SqlKeywordPosition.MergeClause)]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN NOT MATCHED THEN INSERT (a) VALUES (s.a) ", SqlKeywordPosition.MergeClause)]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN NOT MATCHED THEN INSERT DEFAULT VALUES ", SqlKeywordPosition.MergeClause)]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN MATCHED THEN UPDATE SET a = s.a ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.MergeClause | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT * FROM t JOIN u ON t.a = u.a ", SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT CASE WHEN a = 1 THEN ", SqlKeywordPosition.Any)]
    [InlineData("GRANT SELECT, INSERT ", SqlKeywordPosition.PermissionList)]
    [InlineData("REVOKE SELECT (a, b) ", SqlKeywordPosition.PermissionList)]
    [InlineData("GRANT SELECT ON dbo.Loan ", SqlKeywordPosition.PermissionTarget)]
    [InlineData("GRANT EXECUTE ON SCHEMA::dbo ", SqlKeywordPosition.PermissionTarget)]
    [InlineData("GRANT EXECUTE ON EXTERNAL MODEL::LibModel ", SqlKeywordPosition.PermissionTarget)]
    [InlineData("GRANT REFERENCES ON XML SCHEMA COLLECTION::dbo.LibSchemas ", SqlKeywordPosition.PermissionTarget)]
    [InlineData("ALTER AUTHORIZATION ON ", SqlKeywordPosition.PermissionOn)]
    [InlineData("ALTER AUTHORIZATION ON SCHEMA::Lib ", SqlKeywordPosition.PermissionTarget)]
    [InlineData("ALTER AUTHORIZATION ON SEARCH PROPERTY LIST::LibProperties ", SqlKeywordPosition.PermissionTarget)]
    [InlineData("ALTER DATABASE AUDIT SPECIFICATION s ADD (SELECT, INSERT ", SqlKeywordPosition.PermissionList)]
    [InlineData("ALTER DATABASE AUDIT SPECIFICATION s ADD (SELECT ON ", SqlKeywordPosition.PermissionOn)]
    [InlineData("CREATE DATABASE AUDIT SPECIFICATION s FOR SERVER AUDIT a ADD (SCHEMA_OBJECT_ACCESS_GROUP), DROP (DELETE ON OBJECT::dbo.Loan ", SqlKeywordPosition.PermissionTarget)]
    [InlineData("GRANT SELECT ON dbo.Loan TO ", SqlKeywordPosition.PermissionGrantee)]
    [InlineData("GRANT SELECT ON dbo.Loan TO LibClerk, ", SqlKeywordPosition.PermissionGrantee)]
    [InlineData("DENY VIEW DEFINITION TO ", SqlKeywordPosition.PermissionGrantee)]
    [InlineData("ALTER AUTHORIZATION ON SCHEMA::Lib TO ", SqlKeywordPosition.PermissionGrantee)]
    [InlineData("ALTER DATABASE AUDIT SPECIFICATION s ADD (SELECT ON dbo.Loan BY ", SqlKeywordPosition.PermissionGrantee)]
    [InlineData("BACKUP DATABASE d TO ", SqlKeywordPosition.OptionItem)]
    [InlineData("BACKUP DATABASE d TO DISK = 'a' MIRROR TO DISK = 'b', ", SqlKeywordPosition.OptionItem)]
    [InlineData("SELECT a INTO #t ", SqlKeywordPosition.SelectIntoTail)]
    [InlineData("FETCH NEXT FROM c ", SqlKeywordPosition.FetchTail)]
    [InlineData("FETCH NEXT FROM GLOBAL c ", SqlKeywordPosition.FetchTail)]
    [InlineData("FETCH NEXT FROM GLOBAL ", SqlKeywordPosition.Any)]
    [InlineData("OPEN GLOBAL ", SqlKeywordPosition.Any)]
    [InlineData("CREATE UNIQUE INDEX ix ON dbo.Loan (CopyNo ", SqlKeywordPosition.IndexKeyTail)]
    [InlineData("CREATE INDEX ix ON dbo.Loan (CopyNo DESC, ReaderId ", SqlKeywordPosition.IndexKeyTail)]
    [InlineData("CREATE TABLE t (a int, CONSTRAINT pk PRIMARY KEY CLUSTERED (a ", SqlKeywordPosition.IndexKeyTail)]
    [InlineData("MERGE t USING s ON CASE WHEN ", SqlKeywordPosition.Predicate)]
    [InlineData("SELECT CASE WHEN ", SqlKeywordPosition.Predicate)]
    [InlineData("BACKUP DATABASE d TO DISK = 'x' WITH ", SqlKeywordPosition.OptionItem)]
    [InlineData("BACKUP LOG d TO DISK = 'x' WITH INIT, STATS = 10, ", SqlKeywordPosition.OptionItem)]
    [InlineData("RESTORE DATABASE d FROM DISK = 'x' WITH MOVE 'a' TO 'b', ", SqlKeywordPosition.OptionItem)]
    [InlineData("BACKUP DATABASE d TO DISK = 'x' WITH ENCRYPTION (ALGORITHM = AES_256, ", SqlKeywordPosition.Any)]
    [InlineData("BACKUP CERTIFICATE c TO FILE = 'x' WITH ", SqlKeywordPosition.Any)]
    [InlineData("CREATE TABLE t (a int REFERENCES dbo.Copy ", SqlKeywordPosition.ReferencesTail)]
    [InlineData("CREATE TABLE t (a int REFERENCES dbo.Copy (CopyNo) ", SqlKeywordPosition.ReferencesTail)]
    [InlineData("CREATE TABLE t (a int REFERENCES u (a) ON DELETE CASCADE ", SqlKeywordPosition.ReferencesTail)]
    [InlineData("CREATE TABLE t (a int REFERENCES u (a) ON DELETE SET NULL ", SqlKeywordPosition.ReferencesTail)]
    [InlineData("ALTER TABLE t ADD CONSTRAINT fk FOREIGN KEY (a) REFERENCES u (a) ON UPDATE NO ACTION ", SqlKeywordPosition.ReferencesTail)]
    [InlineData("GRANT REFERENCES ON t ", SqlKeywordPosition.PermissionTarget)]
    [InlineData("SELECT * FROM t ORDER BY a OFFSET 10 ", SqlKeywordPosition.OffsetTail)]
    [InlineData("SELECT * FROM t ORDER BY a OFFSET @n ", SqlKeywordPosition.OffsetTail)]
    [InlineData("SELECT * FROM t ORDER BY a OFFSET (@n + 1) ", SqlKeywordPosition.OffsetTail)]
    [InlineData("SELECT SUM(a) OVER (ORDER BY a ", SqlKeywordPosition.WindowOrderTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT SUM(a) OVER (PARTITION BY b ORDER BY a DESC ", SqlKeywordPosition.WindowOrderTail | SqlKeywordPosition.OperandTail)]
    [InlineData("SELECT STRING_AGG(a, ',') WITHIN GROUP (ORDER BY a ", SqlKeywordPosition.OrderByTail | SqlKeywordPosition.OperandTail)]
    [InlineData("GRANT EXECUTE ON ", SqlKeywordPosition.PermissionOn)]
    [InlineData("REVOKE SELECT, INSERT ON ", SqlKeywordPosition.PermissionOn)]
    [InlineData("CREATE INDEX ix ON dbo.Loan (CopyNo) WITH (", SqlKeywordPosition.IndexOption)]
    [InlineData("CREATE INDEX ix ON dbo.Loan (CopyNo) WITH (ONLINE = ON, ", SqlKeywordPosition.IndexOption)]
    [InlineData("CREATE UNIQUE NONCLUSTERED INDEX ix ON dbo.Loan (CopyNo) INCLUDE (LoanDate) WHERE CopyNo IS NOT NULL WITH (", SqlKeywordPosition.IndexOption)]
    [InlineData("CREATE TABLE dbo.Loan (CopyNo int, INDEX ix UNIQUE NONCLUSTERED (CopyNo) INCLUDE (LoanDate) WHERE CopyNo > 0 WITH (", SqlKeywordPosition.IndexOption)]
    [InlineData("ALTER TABLE dbo.Loan ADD INDEX ix NONCLUSTERED (CopyNo) WITH (FILLFACTOR = 80, ", SqlKeywordPosition.IndexOption)]
    [InlineData("CREATE JSON INDEX jx ON dbo.Copy (Doc) FOR ('$.title') WITH (", SqlKeywordPosition.IndexOption)]
    [InlineData("SELECT * FROM t TABLESAMPLE (10 ", SqlKeywordPosition.TableSampleTail)]
    [InlineData("SELECT * FROM t TABLESAMPLE SYSTEM (@n ", SqlKeywordPosition.TableSampleTail)]
    [InlineData("SELECT * FROM t PIVOT (SUM(x) ", SqlKeywordPosition.PivotClause)]
    [InlineData("SELECT * FROM t PIVOT (SUM(x) FOR y ", SqlKeywordPosition.PivotClause)]
    [InlineData("SELECT * FROM t UNPIVOT (v ", SqlKeywordPosition.PivotClause)]
    [InlineData("SELECT * FROM t UNPIVOT (v FOR [y] ", SqlKeywordPosition.PivotClause)]
    [InlineData("SELECT a FROM t FOR XML ", SqlKeywordPosition.OptionItem)]
    [InlineData("SELECT a FROM t FOR XML PATH('x'), ROOT('y'), ", SqlKeywordPosition.OptionItem)]
    [InlineData("SELECT a FROM t FOR JSON PATH, ", SqlKeywordPosition.OptionItem)]
    [InlineData("SELECT a FROM t WHERE b = 1 ORDER BY a FOR JSON AUTO, ", SqlKeywordPosition.OptionItem)]
    [InlineData("ALTER TABLE t ADD a int NOT NULL DEFAULT (0), ", SqlKeywordPosition.AlterTableAdd)]
    public void 敘述自己的格子(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 子句錨點只在游標所在的那一句裡找；自己沒有錨點的一句判不出來，不借上一句的。
    /// </summary>
    /// <remarks>
    /// 借到上一句 WHERE 的述詞尾端時，<c>EXEC p @x </c> 之後的 OUTPUT 就不見了。
    /// </remarks>
    [Theory]
    [InlineData("SELECT a FROM t WHERE b = 1\nEXEC p @x ")]
    [InlineData("SELECT a FROM t WHERE b = 1\nPRINT @x ")]
    [InlineData("SELECT a FROM t WHERE b = 1\nRETURN @x ")]
    [InlineData("SELECT a FROM t WHERE b = 1\nRAISERROR('x', 16, 1) ")]
    [InlineData("SELECT a FROM t WHERE b = 1\nTHROW 50000, 'x', 1 ")]
    [InlineData("SELECT a FROM t WHERE b = 1;\nEXEC p @x ")]
    [InlineData("SELECT a FROM t WHERE b = 1\nGO\nEXEC p @x ")]
    [InlineData("SELECT a FROM t WITH (NOLOCK)\nEXEC p @x ")]
    [InlineData("UPDATE t SET a = 1\nEXEC p @x, ")]
    [InlineData("IF @a = 1 PRINT 'x' ")]
    [InlineData("IF @a = 1 EXEC dbo.p ")]
    [InlineData("DROP TABLE IF EXISTS t ")]
    [InlineData("ALTER TABLE t DROP COLUMN IF EXISTS a ")]
    public void 子句錨點不跨到上一句(string textBeforeToken)
    {
        Assert.Equal(SqlKeywordPosition.Any, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 一長串沒有子句關鍵字的敘述不會讓往前一句再問的遞迴把堆疊用完。
    /// </summary>
    [Fact]
    public void 長串沒有錨點的敘述不會把堆疊用完()
    {
        var script = string.Concat(Enumerable.Repeat("DECLARE @a int\nEXEC p @x = 1\n", 20000));

        Assert.Equal(SqlKeywordPosition.Any, SqlKeywordPositionAnalyzer.Analyze(script + "PRINT @a ").Keywords);
    }

    [Fact]
    public void 沒有錨點的一句列得出自己的續寫字()
    {
        Assert.True(AllowedAt("SELECT a FROM t WHERE b = 1\nEXEC p @x ", "OUTPUT"));
    }

    /// <summary>
    /// 能開始一句的字寫在一句中間時不是開頭：WITH 的提示與選項只認明確的語句界線。
    /// </summary>
    [Theory]
    [InlineData("SELECT a FROM t WITH (NOLOCK) ", SqlKeywordPosition.TableSourceTail)]
    [InlineData("UPDATE t\nSET a = 1 ", SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.UpdateSetTail | SqlKeywordPosition.OperandTail)]
    [InlineData("CREATE VIEW v\nWITH SCHEMABINDING\nAS ", SqlKeywordPosition.StatementStart)]
    [InlineData("CREATE PROCEDURE p WITH EXECUTE AS OWNER AS ", SqlKeywordPosition.StatementStart)]
    [InlineData("CREATE OR ALTER PROCEDURE p AS ", SqlKeywordPosition.StatementStart)]
    [InlineData("EXEC p\nCREATE TRIGGER tr ON t AFTER UPDATE AS SET NOCOUNT ", SqlKeywordPosition.SetOptionValue)]
    public void 句中能開始一句的字不切斷這一句(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// CTE 的 WITH 前一句必須以分號結束；接在隱含界線後面的 WITH 是那一句的選項，不是新名字。
    /// </summary>
    [Theory]
    [InlineData("WITH ", SqlCompletionSlot.Name)]
    [InlineData("SELECT 1;\nWITH ", SqlCompletionSlot.Name)]
    [InlineData("IF @a = 1 WITH ", SqlCompletionSlot.Name)]
    [InlineData("CREATE VIEW v AS WITH ", SqlCompletionSlot.Name)]
    [InlineData("EXEC p WITH ", SqlCompletionSlot.Grammar)]
    [InlineData("CREATE VIEW v\nWITH ", SqlCompletionSlot.Grammar)]
    [InlineData("BACKUP DATABASE d TO DISK = 'x' WITH ", SqlCompletionSlot.Grammar)]
    public void CTE只接在明確的語句界線後面(string textBeforeToken, SqlCompletionSlot expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Slot);
    }

    /// <summary>
    /// 寫完一整句、再也接不了別的東西的字之後是下一句的開頭；還接得了字的照舊判不出來。
    /// </summary>
    /// <remarks>
    /// COMMIT 還接 TRAN、WORK，RETURN 還接運算式，BEGIN TRAN 與 THROW 還接變數：
    /// 判成語句開頭就把這些字藏起來了。
    /// </remarks>
    [Theory]
    [InlineData("BREAK ", SqlKeywordPosition.StatementStart)]
    [InlineData("CHECKPOINT ", SqlKeywordPosition.StatementStart)]
    [InlineData("WHILE @i < 10 BEGIN BREAK ", SqlKeywordPosition.StatementStart)]
    [InlineData("WHILE @i < 10 CONTINUE ", SqlKeywordPosition.StatementStart)]
    [InlineData("SELECT 1\nREVERT ", SqlKeywordPosition.StatementStart)]
    [InlineData("IF @a = 1 BREAK ", SqlKeywordPosition.StatementStart | SqlKeywordPosition.IfBodyEnd)]
    [InlineData("COMMIT ", SqlKeywordPosition.Any)]
    [InlineData("RETURN ", SqlKeywordPosition.Any)]
    [InlineData("BEGIN TRAN ", SqlKeywordPosition.Any)]
    [InlineData("THROW ", SqlKeywordPosition.Any)]
    public void 寫完一整句的字之後是下一句(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// IF 只有一句的主體寫完之後接得了 ELSE；這一格的字由產生器給，不手寫。
    /// </summary>
    [Theory]
    [InlineData("IF @a = 1 SELECT 1 ", true)]
    [InlineData("IF @a = 1 SELECT a FROM t WHERE b = 1 ", true)]
    [InlineData("IF @a = 1\n    SELECT a FROM t WHERE b = 1\n", true)]
    [InlineData("IF EXISTS (SELECT 1 FROM t) UPDATE t SET a = 1 ", true)]
    [InlineData("IF @a = 1 SET NOCOUNT ON ", true)]
    [InlineData("IF @a = 1 IF @b = 1 SELECT 1 ", true)]
    [InlineData("IF @a = 1 SELECT CASE WHEN b = 1 THEN 1 END ", true)]
    [InlineData("SELECT 1 ", false)]
    [InlineData("IF @a = 1 ", false)]
    [InlineData("IF @a = 1 SELECT ", false)]
    [InlineData("IF @a = 1 SELECT 1\nSELECT 2 ", false)]
    [InlineData("IF @a = 1 SELECT 1 ELSE SELECT 2 ", false)]
    [InlineData("IF @a = 1 SELECT 1 ELSE ", false)]
    [InlineData("IF @a = 1 SELECT 1; ", true)]
    [InlineData("IF @a = 1 PRINT 'x';\n", true)]
    [InlineData("IF @a = 1 BEGIN SELECT 1 END; ", true)]
    [InlineData("IF @a = 1 SELECT 1; SELECT 2; ", false)]
    [InlineData("SELECT 1; ", false)]
    [InlineData("IF @a = 1 BEGIN TRY ", false)]
    [InlineData("WHILE @a = 1 SELECT 1 ", false)]
    [InlineData("IF @a = 1 IF @b = 1 ", false)]
    [InlineData("IF @a = 1 UPDATE dbo.Loan\nSET CopyNo = 1 ", true)]
    public void IF只有一句的主體寫完之後接得了ELSE(string textBeforeToken, bool expected)
    {
        Assert.Equal(expected, AllowedAt(textBeforeToken, "ELSE"));
    }

    [Fact]
    public void IF主體寫完只多出ELSE()
    {
        Assert.Equal(
            SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OperandTail | SqlKeywordPosition.IfBodyEnd,
            SqlKeywordPositionAnalyzer.Analyze("IF @a = 1 SELECT 1 ").Keywords);
        Assert.False(AllowedAt("IF @a = 1 SELECT 1 ", "TRY"));

        var gained = SqlKeywordCatalog.All
            .Where(keyword => (SqlKeywordCatalog.GetPositions(keyword) &
                               (SqlKeywordPosition.IfBodyEnd | SqlKeywordPosition.StatementStart)) ==
                              SqlKeywordPosition.IfBodyEnd)
            .ToArray();

        Assert.Equal(new[] { "ELSE" }, gained);
    }

    /// <summary>
    /// FROM 接不接資料來源由它所屬的動詞決定：只有 SELECT、UPDATE、DELETE 的 FROM 是資料來源；REVOKE 的 FROM 之後是主體，
    /// BACKUP／RESTORE 的是裝置清單。不是關鍵字的 COPY 在一句的開頭才是動詞，寫在 FROM 之後的 Copy 是資料表。
    /// </summary>
    /// <remarks>
    /// 動詞往回找時，權限清單的一項（<c>REVOKE SELECT</c>）與 <c>WITH TIES</c> 的 WITH 不是動詞，
    /// <c>SELECT (a)</c> 的 SELECT 接括號時仍是動詞。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM ", SqlKeywordPosition.DataSource)]
    [InlineData("SELECT (CopyNo) AS c FROM ", SqlKeywordPosition.DataSource)]
    [InlineData("SELECT TOP (5) WITH TIES CopyNo FROM ", SqlKeywordPosition.DataSource)]
    [InlineData("UPDATE l SET CopyNo = 1 FROM ", SqlKeywordPosition.DataSource)]
    [InlineData("UPDATE l WITH (ROWLOCK)\nSET CopyNo = 1\nFROM ", SqlKeywordPosition.DataSource)]
    [InlineData("DELETE FROM ", SqlKeywordPosition.DataSource)]
    [InlineData("FETCH NEXT FROM ", SqlKeywordPosition.Any)]
    [InlineData("RESTORE DATABASE LibArchive FROM ", SqlKeywordPosition.OptionItem)]
    [InlineData("REVOKE SELECT ON dbo.Loan FROM ", SqlKeywordPosition.PermissionGrantee)]
    [InlineData("REVOKE SELECT (CopyNo) ON dbo.Loan FROM ", SqlKeywordPosition.PermissionGrantee)]
    [InlineData("BULK INSERT dbo.Loan FROM ", SqlKeywordPosition.Any)]
    [InlineData("CREATE LOGIN Lib_Reader FROM ", SqlKeywordPosition.Any)]
    [InlineData("BULK INSERT dbo.Loan FROM 'x' ", SqlKeywordPosition.Any)]
    [InlineData("RESTORE DATABASE LibArchive FROM DISK = 'a', ", SqlKeywordPosition.OptionItem)]
    [InlineData("COPY INTO dbo.Loan FROM ", SqlKeywordPosition.Any)]
    [InlineData("SELECT 1\nCOPY INTO dbo.Loan FROM ", SqlKeywordPosition.Any)]
    [InlineData("SELECT Copy.CopyNo FROM ", SqlKeywordPosition.DataSource)]
    [InlineData("FETCH NEXT FROM LoanCursor ", SqlKeywordPosition.FetchTail)]
    [InlineData("SELECT * FROM dbo.Loan, ", SqlKeywordPosition.DataSource)]
    public void FROM接不接資料來源由所屬的動詞決定(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }
}
