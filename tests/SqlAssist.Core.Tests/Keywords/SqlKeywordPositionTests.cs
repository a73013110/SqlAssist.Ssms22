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
    [InlineData("SELECT a ", SqlKeywordPosition.SelectListTail)]
    [InlineData("SELECT * FROM ", SqlKeywordPosition.DataSource)]

    // 樣板是 SELECT * FROM t {關鍵字}，但那一行的同一個位置也是別名的位置，
    // 而別名一定寫在同一行——換行之後才是純粹的資料來源尾端。
    // 兩者的分野見「資料來源同一行的下一格可能是別名」。
    // 換行還會多帶一個位元進來，見「換行之後的子句尾端也是下一句的開頭」。
    [InlineData("SELECT * FROM t\r\n",
        SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.StatementStart)]
    [InlineData("SELECT * FROM t WHERE ", SqlKeywordPosition.Predicate)]
    [InlineData("SELECT * FROM t WHERE a = 1 ", SqlKeywordPosition.ExpressionTail)]
    [InlineData("SELECT * FROM t ORDER ", SqlKeywordPosition.ByAnchor)]
    [InlineData("SELECT * FROM t ORDER BY a ", SqlKeywordPosition.OrderByTail)]
    [InlineData("SELECT * FROM t GROUP BY a ", SqlKeywordPosition.GroupByTail)]
    [InlineData("SELECT * FROM t GROUP BY a, b ", SqlKeywordPosition.GroupByTail)]
    [InlineData("SELECT * FROM t GROUP BY ROLLUP(a) ", SqlKeywordPosition.GroupByTail)]
    [InlineData("CREATE ", SqlKeywordPosition.DdlObject)]
    [InlineData("BEGIN ", SqlKeywordPosition.BlockStart)]
    [InlineData("SET ", SqlKeywordPosition.SetTarget)]
    [InlineData("INSERT ", SqlKeywordPosition.InsertTarget)]
    public void 分析器認得樣板對應的位置(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
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
        SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.ExpressionTail)]
    [InlineData("SELECT * FROM t WHERE (a = 1) ", SqlKeywordPosition.ExpressionTail)]
    [InlineData("SELECT * FROM t WHERE x IN (SELECT y FROM u) ", SqlKeywordPosition.ExpressionTail)]
    [InlineData("SELECT COUNT(*) ", SqlKeywordPosition.SelectListTail)]
    [InlineData("SELECT * FROM t WITH (NOLOCK) ", SqlKeywordPosition.TableSourceTail)]
    [InlineData("SELECT * FROM (t1 JOIN t2 ON t1.x = t2.x) ", SqlKeywordPosition.TableSourceTail)]
    [InlineData("INSERT INTO t (a, b) ", SqlKeywordPosition.TableSourceTail)]
    [InlineData(";WITH c AS (SELECT 1 AS a) ", SqlKeywordPosition.StatementStart)]
    public void 括號是一個運算元不是一段路(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 文法強制別名的右括號後面只能是別名：衍生資料表與 PIVOT、UNPIVOT。
    /// </summary>
    /// <remarks>
    /// <c>FROM (SELECT 1)</c> 少了別名就是語法錯誤，所以那一格一定是名字。
    /// 括號是什麼由它<b>前面</b>那個字決定：同樣裝著一個 SELECT，
    /// 接在 <c>IN</c> 後面的那個是運算式，後面不接別名。換行也不改變這件事。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM (SELECT 1 AS a) ")]
    [InlineData("SELECT * FROM t JOIN (SELECT 1 AS a) ")]
    [InlineData("SELECT * FROM t CROSS APPLY (SELECT 1 AS a) ")]
    [InlineData("SELECT * FROM ((SELECT 1 AS a)) ")]
    [InlineData("SELECT * FROM (VALUES (1), (2)) ")]
    [InlineData("MERGE dbo.T AS t USING (SELECT 1 AS a) ")]
    [InlineData("SELECT * FROM (SELECT 1)\n")]
    [InlineData("SELECT * FROM t PIVOT (SUM(x) FOR y IN ([a])) ")]
    [InlineData("SELECT * FROM t UNPIVOT (v FOR y IN (a, b)) ")]
    [InlineData("SELECT * FROM t PIVOT (SUM(x) FOR y IN ([a])) AS ")]
    public void 必填別名的括號之後一定是名字(string textBeforeToken)
    {
        var caret = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken);

        Assert.Equal(SqlCompletionSlot.Name, caret.Slot);
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

        if (!isAlias)
        {
            Assert.Equal(SqlKeywordPosition.Any, caret.Keywords);
        }
    }

    /// <summary>
    /// 資料來源之後、別名還沒寫，而且沒有換行——那一格可能是別名。
    /// </summary>
    /// <remarks>
    /// 沒有 <c>AS</c> 的別名與打到一半的子句關鍵字在剖析器眼中一模一樣，
    /// 唯一分得開的線索是換行：別名一定寫在資料來源的同一行，
    /// 而子句與下一個敘述幾乎總是換行寫。別名寫完之後接的是資料來源尾端。
    ///
    /// 資料來源的後綴屬於同一項：別名寫在 <c>FOR SYSTEM_TIME …</c> 與資料表值函式的
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
    [InlineData("SELECT COUNT(*) ")]
    [InlineData("SELECT N'x' ")]
    [InlineData("SELECT CASE WHEN a = 1 THEN 2 END ")]
    [InlineData("SELECT DISTINCT a ")]
    [InlineData("SELECT TOP (1) a ")]
    [InlineData("SELECT * FROM (SELECT a ")]
    public void 選取清單同一行的下一格也可能是別名(string textBeforeToken)
    {
        var caret = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken);

        Assert.Equal(SqlCompletionSlot.MaybeName, caret.Slot);
        Assert.Equal(SqlKeywordPosition.SelectListTail, caret.Keywords);
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
    [InlineData("SELECT TOP 10 a ", SqlKeywordPosition.SelectListTail)]
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
    /// 括號裡的另一個運算式不受外層 CASE 影響。
    /// </remarks>
    [Theory]
    [InlineData("SELECT CASE WHEN a ", SqlKeywordPosition.CaseArm)]
    [InlineData("SELECT CASE WHEN a = 1 ", SqlKeywordPosition.CaseArm)]
    [InlineData("SELECT CASE WHEN a = 1 AND b IN (1, 2) ", SqlKeywordPosition.CaseArm)]
    [InlineData("SELECT CASE a WHEN 1 ", SqlKeywordPosition.CaseArm)]
    [InlineData("SELECT CASE WHEN a = 1 THEN b ", SqlKeywordPosition.CaseBody)]
    [InlineData("SELECT CASE WHEN a = 1 THEN b ELSE c ", SqlKeywordPosition.CaseBody)]
    [InlineData("SELECT CASE a ", SqlKeywordPosition.CaseBody)]
    [InlineData("SELECT CASE WHEN a = 1 THEN CASE WHEN b = 2 THEN 3 END ", SqlKeywordPosition.CaseBody)]
    [InlineData("SELECT CASE WHEN a = 1 THEN 2 END ", SqlKeywordPosition.SelectListTail)]
    [InlineData("SELECT * FROM t WHERE x = CASE WHEN a = 1 THEN 2 END ", SqlKeywordPosition.ExpressionTail)]
    [InlineData("SELECT CASE WHEN a IN (1, ", SqlKeywordPosition.SelectList)]
    [InlineData("BEGIN SELECT 1 END ", SqlKeywordPosition.Any)]
    [InlineData("IF @a = 1 PRINT 'x' ELSE PRINT 'y' ", SqlKeywordPosition.Any)]
    public void CASE的位置(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Keywords);
    }

    /// <summary>
    /// 使用者正要取新名字的那一格：建立敘述、CTE 與 SELECT … INTO。
    /// </summary>
    /// <remarks>
    /// <c>CREATE OR ALTER</c> 也常是既有物件，所以只是「可能是名字」；
    /// <c>ALTER</c>、<c>INSERT INTO</c> 要的是既有物件，照常。
    /// WITH 只認敘述開頭的那一個：資料表提示與模組選項的 WITH 不是 CTE。
    /// </remarks>
    [Theory]
    [InlineData("CREATE PROCEDURE ", SqlCompletionSlot.Name)]
    [InlineData("CREATE PROC dbo.", SqlCompletionSlot.Name)]
    [InlineData("CREATE TABLE ", SqlCompletionSlot.Name)]
    [InlineData("CREATE UNIQUE CLUSTERED INDEX ", SqlCompletionSlot.Name)]
    [InlineData("CREATE SCHEMA ", SqlCompletionSlot.Name)]
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
    public void 資料行定義的起點可能是名字(string textBeforeToken, SqlKeywordPosition expected)
    {
        var caret = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken);

        Assert.Equal(SqlCompletionSlot.MaybeName, caret.Slot);
        Assert.Equal(expected, caret.Keywords);
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
    [InlineData("SELECT @x ", SqlKeywordPosition.SelectListTail)]
    [InlineData("SELECT * FROM t WHERE a = @x ", SqlKeywordPosition.ExpressionTail)]
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
    /// </remarks>
    [Theory]
    [InlineData("SELECT a, ", SqlKeywordPosition.SelectList)]
    [InlineData("SELECT * FROM t1, ", SqlKeywordPosition.DataSource)]
    [InlineData("SELECT * FROM t WHERE a IN (1, ", SqlKeywordPosition.Predicate)]
    [InlineData("SELECT * FROM t ORDER BY a, ", SqlKeywordPosition.OrderByColumn)]
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
    [InlineData("SELECT * FROM t ORDER BY a ", SqlKeywordPosition.OrderByTail)]

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

    // 資料行定義的起點是新名字或條件約束，沒有既有物件是對的。
    [InlineData("CREATE TABLE t (", false)]
    [InlineData("CREATE TABLE t (a int, ", false)]

    // 判不出位置時是 Any，那是 fail-open：名稱照列。資料行型別之後就落在這裡。
    [InlineData("SELECT * FROM t WHERE a = 1 AND ", true)]
    [InlineData("CREATE TABLE t (a int ", true)]

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
    [InlineData("UPDATE t SET a ", SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.ExpressionTail)]
    [InlineData("UPDATE TOP (5) t WITH (TABLOCK) SET a ", SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.ExpressionTail)]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN MATCHED THEN UPDATE SET a ", SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.ExpressionTail)]
    [InlineData("ALTER DATABASE CURRENT SET RECOVERY ", SqlKeywordPosition.SetOptionValue)]
    [InlineData("UPDATE t SET a = 1 ", SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.ExpressionTail)]
    [InlineData("UPDATE t SET a = 1, ", SqlKeywordPosition.Any)]
    [InlineData("UPDATE t SET a = 1, b ", SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.ExpressionTail)]
    [InlineData("SET NOCOUNT ON SELECT a FROM t ", SqlKeywordPosition.TableSourceTail)]
    public void SET之後的選項名稱(string textBeforeToken, SqlKeywordPosition expected)
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
    /// <c>SELECT C</c> 的清單裡因此有 <c>CURSOR</c>、<c>WHERE F</c> 有 <c>FILLFACTOR</c>。
    /// 整個藏起來也不行：判不出位置的地方正是這些字真正的用處。
    /// 走的是產品的過濾路徑，不只比旗標。
    /// </remarks>
    [Theory]
    [InlineData("SELECT C", "CURSOR", false)]
    [InlineData("SELECT * FROM t WHERE F", "FILLFACTOR", false)]
    [InlineData("SELECT * FROM t ORDER BY P", "PUBLIC", false)]
    [InlineData("P", "PLAN", false)]
    [InlineData("SELECT * FROM t C", "CASCADE", false)]
    [InlineData("UPDATE t SET S", "STOPLIST", false)]
    [InlineData("CREATE TABLE t (N", "NOLOCK", false)]
    [InlineData("SELECT * FROM t CROSS A", "APPLY", true)]
    [InlineData("DECLARE c C", "CURSOR", true)]
    [InlineData("CREATE INDEX i ON t (a) WITH F", "FILLFACTOR", true)]
    [InlineData("GRANT SELECT ON t TO P", "PUBLIC", true)]
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
    [InlineData("SELECT GETDATE()\n")]
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
        var expected = SqlKeywordPosition.SelectListTail | SqlKeywordPosition.StatementStart;
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
    [InlineData("SELECT dbo.fn_Fee('') ", SqlKeywordPosition.SelectListTail)]
    [InlineData("SELECT dbo.fn_Fee('') /* 同一行 */ ", SqlKeywordPosition.SelectListTail)]
    [InlineData("SELECT dbo.fn_Fee('\n') ", SqlKeywordPosition.SelectListTail)]
    [InlineData("SELECT [Copy\nNo] ", SqlKeywordPosition.SelectListTail)]
    [InlineData("SELECT\n", SqlKeywordPosition.SelectList)]
    [InlineData("SELECT 1,\n", SqlKeywordPosition.SelectList)]
    [InlineData("SELECT dbo.fn_Fee(1\n", SqlKeywordPosition.SelectListTail)]
    [InlineData("SELECT COALESCE(dbo.fn_Fee(''), 1\n", SqlKeywordPosition.SelectListTail)]
    [InlineData("SELECT (SELECT 1\n", SqlKeywordPosition.SelectListTail)]
    [InlineData(";WITH LoanFees AS (SELECT 1\n", SqlKeywordPosition.SelectListTail)]
    [InlineData("SELECT * FROM (SELECT 1\n", SqlKeywordPosition.SelectListTail)]
    [InlineData("SELECT * FROM dbo.Loan WHERE ReaderId IN (1\n", SqlKeywordPosition.ExpressionTail)]
    [InlineData("SELECT (SELECT CopyNo FROM dbo.Loan\n", SqlKeywordPosition.TableSourceTail)]
    [InlineData("SELECT (SELECT CopyNo FROM dbo.Loan ORDER BY CopyNo\n", SqlKeywordPosition.OrderByTail)]
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
    [InlineData("SELECT 1.", SqlKeywordPosition.SelectListTail)]
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
}
