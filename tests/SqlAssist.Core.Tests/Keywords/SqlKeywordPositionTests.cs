using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Snippets;
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
    // 兩者的分野見「資料來源之後的別名位置不接受任何關鍵字」。
    // 換行還會多帶一個位元進來，見「換行之後的子句尾端也是下一句的開頭」。
    [InlineData("SELECT * FROM t\r\n",
        SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.StatementStart)]
    [InlineData("SELECT * FROM t WHERE ", SqlKeywordPosition.Predicate)]
    [InlineData("SELECT * FROM t WHERE a = 1 ", SqlKeywordPosition.ExpressionTail)]
    [InlineData("SELECT * FROM t ORDER ", SqlKeywordPosition.ByAnchor)]
    [InlineData("SELECT * FROM t ORDER BY a ", SqlKeywordPosition.OrderByTail)]
    [InlineData("CREATE ", SqlKeywordPosition.DdlObject)]
    [InlineData("BEGIN ", SqlKeywordPosition.BlockStart)]
    [InlineData("SET ", SqlKeywordPosition.SetTarget)]
    [InlineData("INSERT ", SqlKeywordPosition.InsertTarget)]
    public void 分析器認得樣板對應的位置(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken));
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
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken));
    }

    /// <summary>
    /// 衍生資料表的右括號後面文法上只能是別名。
    /// </summary>
    /// <remarks>
    /// <c>FROM (SELECT 1)</c> 少了別名就是語法錯誤，所以那裡沒有任何關鍵字是對的。
    /// 括號是什麼由它<b>前面</b>那個字決定：同樣裝著一個 SELECT，
    /// 接在 <c>IN</c> 後面的那個是運算式，後面不接別名。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM (SELECT 1 AS a) ")]
    [InlineData("SELECT * FROM t JOIN (SELECT 1 AS a) ")]
    [InlineData("SELECT * FROM t CROSS APPLY (SELECT 1 AS a) ")]
    [InlineData("SELECT * FROM ((SELECT 1 AS a)) ")]
    [InlineData("SELECT * FROM (VALUES (1), (2)) ")]
    [InlineData("MERGE dbo.T AS t USING (SELECT 1 AS a) ")]
    public void 衍生資料表之後不接受任何關鍵字(string textBeforeToken)
    {
        Assert.Equal(SqlKeywordPosition.None, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken));
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
    [InlineData("CREATE PROCEDURE dbo.p AS ", false)]
    [InlineData("CREATE PROCEDURE dbo.p @a int AS ", false)]
    [InlineData("CREATE VIEW v AS ", false)]
    [InlineData("CREATE FUNCTION f() RETURNS TABLE AS ", false)]
    [InlineData("CREATE TRIGGER t ON dbo.T AFTER INSERT AS ", false)]
    [InlineData("EXECUTE AS ", false)]
    public void AS之後是別名還是別的東西(string textBeforeToken, bool isAlias)
    {
        var expected = isAlias ? SqlKeywordPosition.None : SqlKeywordPosition.Any;

        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken));
    }

    /// <summary>
    /// 資料來源之後、別名還沒寫，而且沒有換行——那是別名的位置。
    /// </summary>
    /// <remarks>
    /// 沒有 <c>AS</c> 的別名與打到一半的子句關鍵字在剖析器眼中一模一樣，
    /// 唯一分得開的線索是換行：別名一定寫在資料來源的同一行，
    /// 而子句與下一個敘述幾乎總是換行寫。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM CTE_TEST ")]
    [InlineData("SELECT * FROM dbo.PUBLISHER ")]
    [InlineData("SELECT * FROM a INNER JOIN dbo.Cat_BookCopy ")]
    [InlineData("SELECT * FROM dbo.a, dbo.b ")]
    [InlineData("SELECT * FROM [dbo].[PUBLISHER] ")]
    public void 資料來源之後的別名位置不接受任何關鍵字(string textBeforeToken)
    {
        Assert.Equal(SqlKeywordPosition.None, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken));
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
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken));
    }

    /// <summary>
    /// 選取清單的別名不比照辦理。
    /// </summary>
    /// <remarks>
    /// <c>SELECT PublCode FROM …</c> 是最常打的一行，而 <c>PublCode</c> 之後同樣
    /// 只有一個名稱單位。連選取清單一起收掉的話，換來的是 <c>FROM</c> 打不出來。
    /// </remarks>
    [Theory]
    [InlineData("SELECT PublCode ")]
    [InlineData("SELECT a, b ")]
    public void 選取清單尾端照常(string textBeforeToken)
    {
        Assert.Equal(
            SqlKeywordPosition.SelectListTail,
            SqlKeywordPositionAnalyzer.Analyze(textBeforeToken));
    }

    /// <summary>
    /// 變數與參數的名字是使用者自己取的，而擴充完全不提供變數名稱。
    /// </summary>
    /// <remarks>
    /// 位置分析拿到的是「不含正在輸入的那個詞元」的文字，所以 <c>@pub</c> 打到一半時
    /// 這裡看到的是 <c>@</c>。名字打完之後就恢復正常。
    /// </remarks>
    [Theory]
    [InlineData("DECLARE @", SqlKeywordPosition.None)]
    [InlineData("SELECT @", SqlKeywordPosition.None)]
    [InlineData("SELECT @@", SqlKeywordPosition.None)]
    [InlineData("SELECT * FROM t WHERE a = @", SqlKeywordPosition.None)]
    [InlineData("SELECT @x ", SqlKeywordPosition.SelectListTail)]
    public void 變數名稱的位置不接受任何關鍵字(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken));
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
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken));
    }

    /// <summary>
    /// ORDER BY／GROUP BY 要的那個欄位，以及 ALTER TABLE 的三個位置。
    /// </summary>
    /// <remarks>
    /// 這四處以前一律回 <see cref="SqlKeywordPosition.Any"/>，於是 191 個關鍵字與
    /// 45 筆片段全部進場：同一組候選、同一個前綴 <c>C</c>，<c>SELECT C</c> 只有
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
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken));
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
            SqlKeywordPositionAnalyzer.Analyze("SELECT * FROM [FROM]\r\n"));
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

    // ALTER TABLE：SQL Prompt 在 ADD 之後給的就是這幾個字。
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
        var suggestions = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty);

        var matched = SuggestionMatcher
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

    // 反方向：這些位置本來就是要選名稱的，一個都不能少。
    [InlineData("SELECT * FROM ", true)]
    [InlineData("SELECT ", true)]
    [InlineData("SELECT * FROM t WHERE ", true)]
    [InlineData("SELECT * FROM t ORDER BY ", true)]

    // INSERT 之後的 INTO 可以省略，所以那裡的資料表要留著。
    [InlineData("INSERT ", true)]

    // 判不出位置時是 Any，那是 fail-open：名稱照列。CREATE TABLE 的資料行定義
    // 目前就落在這裡——ColumnDefinition 只有產生器認得，分析器回不出來。
    [InlineData("SELECT * FROM t WHERE a = 1 AND ", true)]
    [InlineData("CREATE TABLE t (a int ", true)]
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

        Assert.Equal(expected, SuggestionMatcher.Filter(new[] { table }, context).Count == 1);
    }

    [Fact]
    public void 產生器判不出位置的關鍵字一律放行()
    {
        // FILLFACTOR 這種深層子句字沒有樣板涵蓋得到。分不出位置的代價是多幾個字，
        // 猜錯位置的代價是使用者永遠打不出來——所以 fail-open。
        Assert.Equal(SqlKeywordPosition.Any, SqlKeywordCatalog.GetPositions("FILLFACTOR"));
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

    // 選取清單換行之後接的幾乎總是下一個欄位或 FROM。在那裡放進 64 個語句開頭的字
    // 與 35 筆片段，使用者真正要的欄位就被擠下去了。
    [InlineData("SELECT CopyNo\r\n", "ssf", false)]

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

        var matched = SuggestionMatcher
            .Filter(suggestions, context)
            .Any(suggestion => suggestion.DisplayText == displayText);

        Assert.Equal(expected, matched);
    }
}
