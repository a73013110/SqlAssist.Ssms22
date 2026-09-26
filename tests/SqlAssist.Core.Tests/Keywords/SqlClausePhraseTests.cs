using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Keywords;

/// <summary>
/// 子句片語：產生器探測得到的字，在游標前面是那條尾巴時出現，而且只在那裡出現。
/// </summary>
/// <remarks>
/// 片語的字是剖析器說了算，這裡不驗「有沒有列到某個字」的全部，只驗兩件事：
/// 產生器的探測文字與執行期的比對說的是同一個片語，以及走產品的過濾路徑時
/// 使用者真正會打的那幾個字在、不該在的不在。
/// </remarks>
public sealed class SqlClausePhraseTests
{
    public static TheoryData<string, string> GeneratorProbes()
    {
        var data = new TheoryData<string, string>();

        foreach (var phrase in SqlClausePhraseCatalog.All)
        {
            data.Add(phrase.Pattern, phrase.Probe);
        }

        return data;
    }

    /// <summary>
    /// 產生器探測每個片語用的文字，執行期比對得回同一個片語。
    /// </summary>
    /// <remarks>
    /// 兩邊說的不是同一個位置時，片語的字會出現在錯的地方或永遠不出現。比對還要是確定的：
    /// 探測文字前面墊的是 After 那些位置的樣板，位置分析判不出來就是兩邊說的不是同一個位置。
    /// 同一條尾巴在不同位置上的片語也在這裡分開——比對回別的那一個就代表位置重疊了。
    /// </remarks>
    [Theory]
    [MemberData(nameof(GeneratorProbes))]
    public void 產生器的探測文字比對回同一個片語(string pattern, string probe)
    {
        var tokens = SqlTokenizer.Tokenize(probe);
        var match = SqlClausePhraseCatalog.Match(tokens, probe);

        Assert.NotNull(match);
        Assert.True(match!.IsCertain);
        Assert.Equal(pattern, match.Phrase.Pattern);
        Assert.Equal(probe, match.Phrase.Probe);
    }

    [Fact]
    public void 片語的字不重複()
    {
        foreach (var phrase in SqlClausePhraseCatalog.All)
        {
            Assert.Equal(phrase.Words.Distinct().Count(), phrase.Words.Count);
        }
    }

    /// <summary>
    /// 使用者在 SSMS 內建清單看得到、以前這裡卻一個都沒有的字。
    /// </summary>
    [Theory]
    [InlineData("SET ", "QUOTED_IDENTIFIER", "ANSI_NULLS", "XACT_ABORT", "NOCOUNT", "DATEFORMAT", "STATISTICS", "TRANSACTION", "IDENTITY_INSERT")]
    [InlineData("BEGIN\n    SET ", "NOCOUNT", "XACT_ABORT")]
    [InlineData("SELECT 1\nSET ", "NOCOUNT")]
    [InlineData("IF @a = 1 SET ", "NOCOUNT")]
    [InlineData("CREATE PROCEDURE p AS\nSET ", "NOCOUNT")]
    [InlineData("SET NOCOUNT ", "ON", "OFF")]
    [InlineData("SET STATISTICS ", "IO", "TIME", "XML", "PROFILE")]
    [InlineData("SET STATISTICS IO ", "ON", "OFF")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL ", "READ", "REPEATABLE", "SERIALIZABLE", "SNAPSHOT")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL READ ", "COMMITTED", "UNCOMMITTED")]
    [InlineData("SET DATEFORMAT ", "dmy", "ymd")]
    [InlineData("SET DEADLOCK_PRIORITY ", "LOW", "HIGH")]
    [InlineData("SET IDENTITY_INSERT dbo.Loan ", "ON", "OFF")]
    [InlineData("CREATE ", "TABLE", "SEQUENCE", "SYNONYM", "TYPE", "LOGIN")]
    [InlineData("CREATE OR ALTER ", "PROCEDURE", "VIEW", "FUNCTION", "TRIGGER")]
    [InlineData("ALTER TABLE dbo.Loan ", "ADD", "DROP", "ENABLE", "DISABLE", "SWITCH", "REBUILD")]
    [InlineData("ALTER INDEX IX_Loan ON dbo.Loan ", "REBUILD", "REORGANIZE", "DISABLE")]
    [InlineData("ALTER INDEX ALL ON dbo.Loan ", "REBUILD", "REORGANIZE")]
    [InlineData("CREATE NONCLUSTERED INDEX IX_Loan ON dbo.Loan (CopyNo) ", "INCLUDE", "WHERE", "WITH")]
    [InlineData("CREATE INDEX IX_Loan ON dbo.Loan (CopyNo) INCLUDE (Branch) ", "WHERE", "WITH")]
    [InlineData("CREATE INDEX IX_Loan ON dbo.Loan (CopyNo) WITH (", "ONLINE", "SORT_IN_TEMPDB", "DROP_EXISTING")]
    [InlineData("CREATE INDEX IX_Loan ON dbo.Loan (CopyNo) WITH (ONLINE = ON, ", "SORT_IN_TEMPDB")]
    [InlineData("ALTER DATABASE CURRENT SET ", "READ_COMMITTED_SNAPSHOT", "SINGLE_USER", "RECOVERY")]
    [InlineData("ALTER DATABASE CURRENT SET RECOVERY ", "SIMPLE", "FULL", "BULK_LOGGED")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan ", "AFTER", "INSTEAD", "FOR")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan INSTEAD ", "OF")]
    [InlineData("WAITFOR ", "DELAY", "TIME")]
    [InlineData("DECLARE c CURSOR ", "LOCAL", "FAST_FORWARD", "FOR")]
    [InlineData("SELECT a FROM t FOR XML ", "PATH", "RAW", "AUTO", "EXPLICIT")]
    [InlineData("SELECT a FROM t FOR JSON ", "PATH", "AUTO")]
    [InlineData("SELECT a FROM t ORDER BY a OFFSET 10 ROWS ", "FETCH")]
    [InlineData("SELECT a FROM t ORDER BY a OFFSET 10 ROWS FETCH ", "NEXT", "FIRST")]
    [InlineData("SELECT a FROM t ORDER BY a OFFSET 10 ROWS FETCH NEXT 10 ", "ROWS", "ROW")]
    [InlineData("SELECT a FROM t ORDER BY a OFFSET 10 ROWS FETCH NEXT @n ROWS ", "ONLY")]
    [InlineData("SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN ", "UNBOUNDED", "CURRENT")]
    [InlineData("SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED ", "PRECEDING", "FOLLOWING")]
    [InlineData("SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ", "ROW")]
    [InlineData("SELECT TOP (10) WITH ", "TIES")]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN NOT MATCHED ", "BY", "THEN")]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN NOT MATCHED BY ", "TARGET", "SOURCE")]
    [InlineData("SELECT a FROM t FOR SYSTEM_TIME ", "AS", "BETWEEN", "ALL")]
    [InlineData("SELECT a FROM t FOR SYSTEM_TIME AS ", "OF")]
    [InlineData("SELECT a FROM t GROUP BY ", "ROLLUP", "CUBE", "GROUPING SETS")]
    [InlineData("CREATE TABLE t (a int REFERENCES u (a) ON DELETE ", "CASCADE", "NO", "SET")]
    [InlineData("CREATE TABLE t (a int REFERENCES u (a) ON DELETE NO ", "ACTION")]
    [InlineData("CREATE PROCEDURE p AS\nSET NOCOUNT ", "ON", "OFF")]
    [InlineData("SELECT a FROM t FOR ", "XML", "JSON", "BROWSE", "SYSTEM_TIME")]
    [InlineData("SELECT PUBL_CODE\nFROM dbo.PUBLISHER\nFOR ", "XML", "JSON", "BROWSE", "SYSTEM_TIME")]
    [InlineData("SELECT a FROM t WHERE a = 1 FOR ", "XML", "JSON", "BROWSE")]
    [InlineData("SELECT a FROM t ORDER BY a FOR ", "XML", "JSON")]
    [InlineData("SELECT a FROM t GROUP BY a FOR ", "XML", "JSON")]
    [InlineData("SELECT 1 AS a FOR ", "XML", "JSON")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan FOR ", "INSERT", "UPDATE", "DELETE")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan AFTER ", "INSERT", "UPDATE", "DELETE")]
    [InlineData("DECLARE c CURSOR FOR ", "SELECT")]
    [InlineData("DECLARE c SCROLL CURSOR FOR ", "SELECT")]
    [InlineData("CREATE USER u FOR ", "LOGIN")]
    [InlineData("CREATE TABLE t (a int IDENTITY NOT FOR ", "REPLICATION")]
    public void 片語接得上的字出現在清單裡(string textBeforeToken, params string[] expected)
    {
        var offered = Offered(textBeforeToken);

        Assert.All(expected, word => Assert.Contains(word, offered));
    }

    /// <summary>
    /// 片語比對得到時，這一格的關鍵字只來自片語。
    /// </summary>
    /// <remarks>
    /// 以前 <c>SET DATEFORMAT </c> 與 <c>SET NOCOUNT </c> 同一個位置，清單給的是 ON／OFF；
    /// <c>SET TRANSACTION ISOLATION LEVEL READ </c> 被當成值寫完，列的是下一句的字。
    /// </remarks>
    [Theory]
    [InlineData("SET DATEFORMAT ", "ON")]
    [InlineData("SET LANGUAGE ", "OFF")]
    [InlineData("SET NOCOUNT ", "READ")]
    [InlineData("SET STATISTICS ", "ON")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL READ ", "SELECT")]
    [InlineData("SET ", "SELECT")]
    [InlineData("CREATE INDEX IX_Loan ON dbo.Loan (CopyNo) WITH (", "NOLOCK")]
    [InlineData("ALTER INDEX IX_Loan ON dbo.Loan ", "WHERE")]
    [InlineData("CREATE PROCEDURE p AS\nSET NOCOUNT ", "READ")]
    [InlineData("SELECT a FROM t FOR ", "SELECT")]
    [InlineData("SELECT a FROM t WHERE a = 1 FOR ", "SYSTEM_TIME")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan FOR ", "XML")]
    [InlineData("DECLARE c CURSOR FOR ", "XML")]
    public void 片語比對得到時不列片語以外的關鍵字(string textBeforeToken, string keyword)
    {
        Assert.DoesNotContain(keyword, Offered(textBeforeToken));
    }

    /// <summary>
    /// 片語的字只屬於那條尾巴，不會漏到別的位置。
    /// </summary>
    [Theory]
    [InlineData("UPDATE t SET ", "QUOTED_IDENTIFIER")]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN MATCHED THEN UPDATE SET ", "NOCOUNT")]
    [InlineData("SELECT ", "QUOTED_IDENTIFIER")]
    [InlineData("SELECT * FROM t WHERE ", "REBUILD")]
    [InlineData("", "XACT_ABORT")]
    [InlineData("ALTER TABLE t ALTER ", "SEQUENCE")]
    [InlineData("ALTER TABLE t DROP ", "SYNONYM")]
    [InlineData("DELETE FROM t WHERE CURRENT ", "ROW")]
    public void 片語的字不出現在別的位置(string textBeforeToken, string word)
    {
        Assert.DoesNotContain(word, Offered(textBeforeToken));
    }

    /// <summary>
    /// 封閉的片語只列它的字；不封閉的片語換掉關鍵字，名稱照常。
    /// </summary>
    [Theory]
    [InlineData("SET ", true)]
    [InlineData("SET STATISTICS ", true)]
    [InlineData("ALTER INDEX IX_Loan ON dbo.Loan ", true)]
    [InlineData("SET ROWCOUNT ", true)]
    [InlineData("SET IDENTITY_INSERT ", false)]
    [InlineData("SELECT a FROM t GROUP BY ", false)]
    [InlineData("SELECT a FROM t FOR ", true)]
    [InlineData("CREATE TRIGGER tr ON t FOR ", true)]
    [InlineData("SELECT NEXT VALUE FOR ", false)]
    [InlineData("ALTER TABLE t ADD CONSTRAINT df DEFAULT 0 FOR ", false)]
    [InlineData("CREATE SYNONYM s FOR ", false)]
    public void 封閉的片語換掉整份清單(string textBeforeCaret, bool closed)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.NotNull(context.ClausePhrase);
        Assert.Equal(closed, context.ClausePhrase!.IsClosed);
        Assert.Equal(closed, context.Target == CompletionTarget.ClauseKeyword);
    }

    /// <summary>
    /// 前一格判不出位置時片語只加字：片語的字與位置的整份關鍵字都在，清單不封閉。
    /// </summary>
    /// <remarks>
    /// 游標指令的選項之後、<c>DESC</c> 之後、IF 條件之後，位置分析都判不出來。
    /// 把那裡的 FOR 當成查詢之後的 FOR 並封閉清單的話，游標要的 SELECT 就不見了。
    /// </remarks>
    [Theory]
    [InlineData("DECLARE c CURSOR LOCAL FAST_FORWARD FOR ", "SELECT", "XML")]
    [InlineData("SELECT a FROM t ORDER BY a DESC FOR ", "XML", "SELECT")]
    [InlineData("IF @a = 1 SET ", "NOCOUNT", "ROWCOUNT")]
    public void 前一格判不出位置時片語只加字(string textBeforeCaret, string phraseWord, string catalogWord)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var offered = Offered(textBeforeCaret);

        Assert.False(context.ClausePhrase!.IsCertain);
        Assert.NotEqual(CompletionTarget.ClauseKeyword, context.Target);
        Assert.Contains(phraseWord, offered);
        Assert.Contains(catalogWord, offered);
    }

    /// <summary>
    /// 只加字時，目錄與片語都有的字只列一次。
    /// </summary>
    [Fact]
    public void 只加字時同名的關鍵字不重複()
    {
        var offered = Offered("IF @a = 1 SET TRANSACTION ISOLATION LEVEL ");

        Assert.Single(offered, word => word == "READ");
    }

    /// <summary>
    /// <c>WITH (</c> 在資料表之後仍是資料表提示：片語只認 CREATE INDEX 的那一個。
    /// </summary>
    [Fact]
    public void 資料表之後的WITH左括號仍是資料表提示()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("SELECT * FROM t WITH (");

        Assert.Equal(CompletionTarget.TableHint, context.Target);
        Assert.Null(context.ClausePhrase);
    }

    /// <summary>
    /// 片語的字不進自動大寫與方括號判定：PATH、TIME、TYPE 是很常見的資料行名稱。
    /// </summary>
    [Theory]
    [InlineData("QUOTED_IDENTIFIER")]
    [InlineData("PATH")]
    [InlineData("REBUILD")]
    public void 片語的字不是目錄裡的關鍵字(string word)
    {
        Assert.False(SqlKeywordCatalog.IsKeyword(word));
        Assert.False(SqlKeywordCatalog.TryGetCanonical(word.ToLowerInvariant(), out _));
        Assert.False(SqlKeywordCatalog.IsReservedIdentifier(word));
    }

    /// <summary>走產品的過濾路徑：候選清單加上片語的字，再做上下文過濾。</summary>
    private static string[] Offered(string textBeforeToken)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeToken);
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(context.ClausePhrase?.Suggestions ?? Enumerable.Empty<SqlSuggestion>());

        return SuggestionContextFilter.Filter(candidates, context)
            .Where(suggestion => suggestion.Kind == SuggestionKind.Keyword)
            .Select(suggestion => suggestion.DisplayText)
            .ToArray();
    }
}
