using System;
using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
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

        foreach (var phrase in SqlClausePhraseCatalog.All.Where(phrase => !phrase.IsAdditive))
        {
            data.Add(phrase.Pattern, phrase.Probe);
        }

        return data;
    }

    public static TheoryData<string> AdditiveProbes()
    {
        var data = new TheoryData<string>();

        foreach (var phrase in SqlClausePhraseCatalog.All.Where(phrase => phrase.IsAdditive))
        {
            data.Add(phrase.Probe);
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
    /// 只認位置的片語，探測文字就是位置的樣板。
    /// </remarks>
    [Theory]
    [MemberData(nameof(GeneratorProbes))]
    public void 產生器的探測文字比對回同一個片語(string pattern, string probe)
    {
        var match = SqlKeywordPositionAnalyzer.Analyze(probe).Phrase;

        Assert.NotNull(match);
        Assert.True(match!.IsCertain);
        Assert.Equal(pattern, match.Phrase.Pattern);
        Assert.Equal(probe, match.Phrase.Probe);
    }

    /// <summary>
    /// 附加片語的探測文字比對回它自己，而且只是「可能」。
    /// </summary>
    /// <remarks>
    /// 附加片語只補關鍵字目錄給不了的片語開頭，不能把其他字藏起來；探測文字比對到別的片語時，
    /// 附加的字會被那個片語蓋掉而永遠不出現。
    /// </remarks>
    [Theory]
    [MemberData(nameof(AdditiveProbes))]
    public void 附加片語的探測文字比對回自己而且只是可能(string probe)
    {
        var match = SqlKeywordPositionAnalyzer.Analyze(probe).Phrase;

        Assert.NotNull(match);
        Assert.False(match!.IsCertain);
        Assert.True(match.Phrase.IsAdditive);
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
    [InlineData("CREATE ", "OR")]
    [InlineData("CREATE OR ", "ALTER")]
    [InlineData("CREATE OR ALTER ", "PROCEDURE", "VIEW", "FUNCTION", "TRIGGER")]
    [InlineData("EXEC ", "AS")]
    [InlineData("EXECUTE ", "AS")]
    [InlineData("CREATE PROCEDURE p WITH EXECUTE ", "AS")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan WITH ", "ENCRYPTION", "EXECUTE", "SCHEMABINDING", "NATIVE_COMPILATION")]
    [InlineData("CREATE TRIGGER tr ON DATABASE WITH ENCRYPTION, ", "EXECUTE")]
    [InlineData("CREATE PROCEDURE dbo.p @a int WITH ", "ENCRYPTION", "RECOMPILE", "EXECUTE")]
    [InlineData("CREATE PROCEDURE p WITH RECOMPILE, ", "ENCRYPTION")]
    [InlineData("CREATE PROCEDURE p WITH EXECUTE AS ", "CALLER", "OWNER", "SELF")]
    [InlineData("CREATE FUNCTION dbo.f () RETURNS int WITH ", "SCHEMABINDING", "RETURNS", "CALLED", "INLINE")]
    [InlineData("CREATE FUNCTION dbo.f () RETURNS int WITH RETURNS NULL ON ", "NULL")]
    [InlineData("CREATE VIEW dbo.v WITH ", "SCHEMABINDING", "VIEW_METADATA", "ENCRYPTION")]
    [InlineData("ALTER TABLE dbo.Loan ", "ADD", "DROP", "ENABLE", "DISABLE", "SWITCH", "REBUILD")]
    [InlineData("ALTER INDEX IX_Loan ON dbo.Loan ", "REBUILD", "REORGANIZE", "DISABLE")]
    [InlineData("ALTER INDEX ALL ON dbo.Loan ", "REBUILD", "REORGANIZE")]
    [InlineData("CREATE NONCLUSTERED INDEX IX_Loan ON dbo.Loan (CopyNo) ", "INCLUDE", "WHERE", "WITH")]
    [InlineData("CREATE INDEX IX_Loan ON dbo.Loan (CopyNo) INCLUDE (Branch) ", "WHERE", "WITH")]
    [InlineData("CREATE INDEX IX_Loan ON dbo.Loan (CopyNo) WITH (", "ONLINE", "SORT_IN_TEMPDB", "DROP_EXISTING")]
    [InlineData("CREATE INDEX IX_Loan ON dbo.Loan (CopyNo) WITH (ONLINE = ON, ", "SORT_IN_TEMPDB")]
    [InlineData("CREATE INDEX IX_Loan ON dbo.Loan (CopyNo) WHERE CopyNo IS NOT NULL WITH (", "ONLINE", "FILLFACTOR")]
    [InlineData("CREATE INDEX IX_Loan ON dbo.Loan (CopyNo) INCLUDE (Branch) WHERE CopyNo > 0 WITH (ONLINE = ON, ", "SORT_IN_TEMPDB")]
    [InlineData("SELECT a FROM t FOR XML PATH('Copy'), ", "TYPE", "ROOT", "ELEMENTS")]
    [InlineData("SELECT a FROM t FOR XML RAW, ELEMENTS, ", "TYPE", "ROOT")]
    [InlineData("SELECT a FROM t FOR JSON PATH, ", "ROOT", "INCLUDE_NULL_VALUES", "WITHOUT_ARRAY_WRAPPER")]
    [InlineData("SELECT a FROM t TABLESAMPLE (10 ", "PERCENT", "ROWS")]
    [InlineData("GRANT EXECUTE ON ", "SCHEMA", "OBJECT")]
    [InlineData("SELECT * FROM t PIVOT (SUM(x) FOR y ", "IN")]
    [InlineData("ALTER DATABASE CURRENT SET ", "READ_COMMITTED_SNAPSHOT", "SINGLE_USER", "RECOVERY")]
    [InlineData("ALTER DATABASE CURRENT SET RECOVERY ", "SIMPLE", "FULL", "BULK_LOGGED")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan ", "AFTER", "INSTEAD", "FOR")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan INSTEAD ", "OF")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan WITH ENCRYPTION ", "AFTER", "INSTEAD", "FOR")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan WITH ENCRYPTION FOR ", "INSERT", "UPDATE", "DELETE")]
    [InlineData("ALTER TRIGGER tr ON dbo.Loan WITH EXECUTE AS CALLER AFTER ", "INSERT")]
    [InlineData("WAITFOR ", "DELAY", "TIME")]
    [InlineData("DECLARE c CURSOR ", "LOCAL", "FAST_FORWARD", "FOR")]
    [InlineData("DECLARE c CURSOR LOCAL ", "FAST_FORWARD", "READ_ONLY", "FOR")]
    [InlineData("DECLARE c CURSOR LOCAL FAST_FORWARD FOR ", "SELECT")]
    [InlineData("DECLARE c CURSOR FOR SELECT a FROM t FOR ", "UPDATE", "READ")]
    [InlineData("DECLARE c CURSOR FOR SELECT a FROM t FOR UPDATE ", "OF")]
    [InlineData("DECLARE c CURSOR FOR SELECT a FROM t FOR READ ", "ONLY")]
    [InlineData("BACKUP DATABASE d TO DISK = 'x' WITH ", "COMPRESSION", "INIT")]
    [InlineData("BACKUP DATABASE d TO DISK = 'x' WITH COMPRESSION, ", "INIT", "COPY_ONLY")]
    [InlineData("RESTORE DATABASE d FROM DISK = 'x' WITH REPLACE, ", "RECOVERY", "NORECOVERY")]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN ", "MATCHED", "NOT")]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN NOT ", "MATCHED")]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN MATCHED ", "THEN", "AND")]
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
    [InlineData("SELECT DISTINCT TOP 10 PERCENT WITH ", "TIES")]
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
    [InlineData("SELECT a FROM t ORDER BY a DESC FOR ", "XML", "JSON")]
    [InlineData("SELECT a FROM t GROUP BY a FOR ", "XML", "JSON")]
    [InlineData("SELECT 1 AS a FOR ", "XML", "JSON")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan FOR ", "INSERT", "UPDATE", "DELETE")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan AFTER ", "INSERT", "UPDATE", "DELETE")]
    [InlineData("DECLARE c CURSOR FOR ", "SELECT")]
    [InlineData("DECLARE c SCROLL CURSOR FOR ", "SELECT")]
    [InlineData("CREATE USER u FOR ", "LOGIN")]
    [InlineData("CREATE TABLE t (a int IDENTITY NOT FOR ", "REPLICATION")]
    [InlineData("BACKUP DATABASE LibArchive ", "TO")]
    [InlineData("BACKUP DATABASE LibArchive TO ", "DISK", "URL")]
    [InlineData("BACKUP DATABASE @db TO ", "DISK", "URL")]
    [InlineData("BACKUP LOG LibArchive TO ", "DISK", "URL")]
    [InlineData("RESTORE DATABASE LibArchive ", "FROM")]
    [InlineData("RESTORE DATABASE LibArchive FROM ", "DISK", "URL")]
    [InlineData("RESTORE LOG LibArchive FROM ", "DISK", "URL")]
    [InlineData("RESTORE HEADERONLY FROM ", "DISK", "URL")]
    [InlineData("EXEC dbo.usp_Copies WITH ", "RECOMPILE", "RESULT")]
    [InlineData("EXEC dbo.usp_Copies WITH RESULT ", "SETS")]
    [InlineData("EXEC dbo.usp_Copies WITH RECOMPILE, RESULT SETS ", "NONE", "UNDEFINED")]
    [InlineData("EXEC dbo.usp_Copies WITH RESULT SETS (", "AS")]
    [InlineData("EXEC dbo.usp_Copies WITH RESULT SETS ((Branch int), ", "AS")]
    [InlineData("EXEC dbo.usp_Copies WITH RESULT SETS (AS ", "OBJECT", "TYPE", "FOR")]
    [InlineData("EXEC dbo.usp_Copies WITH RESULT SETS (AS FOR ", "XML")]
    [InlineData("EXEC dbo.usp_Copies WITH RESULT SETS ((Branch varchar(10) ", "COLLATE", "NULL", "NOT")]
    [InlineData("EXEC dbo.usp_Copies WITH RESULT SETS ((Branch int NOT ", "NULL")]
    [InlineData("OPEN ", "GLOBAL", "SYMMETRIC", "MASTER")]
    [InlineData("OPEN SYMMETRIC ", "KEY")]
    [InlineData("OPEN SYMMETRIC KEY k ", "DECRYPTION")]
    [InlineData("OPEN SYMMETRIC KEY k DECRYPTION BY ", "CERTIFICATE", "PASSWORD", "SYMMETRIC", "ASYMMETRIC")]
    [InlineData("OPEN SYMMETRIC KEY k DECRYPTION BY ASYMMETRIC ", "KEY")]
    [InlineData("OPEN MASTER KEY ", "DECRYPTION")]
    [InlineData("OPEN MASTER KEY DECRYPTION BY ", "PASSWORD")]
    [InlineData("CLOSE ", "GLOBAL", "SYMMETRIC", "MASTER", "ALL")]
    [InlineData("CLOSE ALL SYMMETRIC ", "KEYS")]
    [InlineData("DEALLOCATE ", "GLOBAL")]
    [InlineData("FETCH NEXT ", "FROM")]
    [InlineData("FETCH NEXT FROM ", "GLOBAL")]
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
    [InlineData("CREATE INDEX IX_Loan ON dbo.Loan (CopyNo) WHERE CopyNo > 0 WITH (", "NOLOCK")]
    [InlineData("SELECT a FROM t FOR XML RAW, ", "INCLUDE_NULL_VALUES")]
    [InlineData("SELECT a FROM t FOR JSON AUTO, ", "ELEMENTS")]
    [InlineData("ALTER INDEX IX_Loan ON dbo.Loan ", "WHERE")]
    [InlineData("CREATE PROCEDURE p AS\nSET NOCOUNT ", "READ")]
    [InlineData("SELECT a FROM t FOR ", "SELECT")]
    [InlineData("SELECT a FROM t WHERE a = 1 FOR ", "SYSTEM_TIME")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan FOR ", "XML")]
    [InlineData("DECLARE c CURSOR FOR ", "XML")]
    [InlineData("DECLARE c CURSOR LOCAL FAST_FORWARD FOR ", "XML")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan WITH ENCRYPTION FOR ", "XML")]
    [InlineData("BACKUP DATABASE d TO DISK = 'x' WITH COMPRESSION, ", "SELECT")]
    [InlineData("MERGE t USING s ON t.a = s.a WHEN ", "EXISTS")]
    [InlineData("SELECT a FROM t ORDER BY a DESC FOR ", "SELECT")]
    [InlineData("IF @a = 1 SET ", "SELECT")]
    [InlineData("CREATE VIEW dbo.v WITH ", "RECOMPILE")]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan WITH ", "VIEW_METADATA")]
    [InlineData("CREATE PROCEDURE p WITH ", "SELECT")]
    [InlineData("OPEN SYMMETRIC KEY k DECRYPTION BY CERTIFICATE ", "KEY")]
    [InlineData("OPEN SYMMETRIC KEY k DECRYPTION BY PASSWORD ", "KEY")]
    [InlineData("FETCH NEXT ", "INTO")]
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
    [InlineData("SELECT CASE WHEN ", "MATCHED")]
    [InlineData("BACKUP CERTIFICATE c TO FILE = 'x' WITH ", "COMPRESSION")]
    [InlineData("SELECT a FROM t WITH ", "TIES")]
    [InlineData("SELECT * FROM t PIVOT (SUM(x) FOR ", "XML")]
    [InlineData("SELECT * FROM t UNPIVOT (v FOR ", "JSON")]
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
    [InlineData("CREATE OR ", true)]
    [InlineData("ALTER INDEX IX_Loan ON dbo.Loan ", true)]
    [InlineData("SET ROWCOUNT ", true)]
    [InlineData("SET IDENTITY_INSERT ", false)]
    [InlineData("SELECT a FROM t GROUP BY ", false)]
    [InlineData("SELECT a FROM t FOR ", true)]
    [InlineData("CREATE TRIGGER tr ON t FOR ", true)]
    [InlineData("CREATE TRIGGER tr ON t WITH ENCRYPTION FOR ", true)]
    [InlineData("DECLARE c CURSOR LOCAL ", true)]
    [InlineData("SELECT NEXT VALUE FOR ", false)]
    [InlineData("ALTER TABLE t ADD CONSTRAINT df DEFAULT 0 FOR ", false)]
    [InlineData("CREATE SYNONYM s FOR ", false)]
    [InlineData("CREATE INDEX IX_Loan ON dbo.Loan (CopyNo) WHERE CopyNo > 0 WITH (", true)]
    [InlineData("SELECT a FROM t FOR XML RAW, ", true)]
    [InlineData("GRANT EXECUTE ON ", false)]
    [InlineData("OPEN ", false)]
    [InlineData("CLOSE ", false)]
    [InlineData("FETCH NEXT FROM ", false)]
    [InlineData("OPEN SYMMETRIC ", true)]
    [InlineData("FETCH NEXT ", true)]
    [InlineData("OPEN SYMMETRIC KEY ", false)]
    [InlineData("CLOSE SYMMETRIC KEY ", false)]
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
    /// 沒有子句關鍵字的一句（<c>PRINT @a</c>）之後，位置分析判不出來。
    /// 把那裡的 SET 當成確定的片語並封閉清單的話，猜錯時要的字就不見了。
    /// </remarks>
    [Theory]
    [InlineData("PRINT @a SET ", "NOCOUNT", "ROWCOUNT")]
    [InlineData("EXEC dbo.p SET ", "XACT_ABORT", "ROWCOUNT")]
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
        var offered = Offered("PRINT @a SET TRANSACTION ISOLATION LEVEL ");

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

    public static TheoryData<string, string, string> PhraseSteps()
    {
        var data = new TheoryData<string, string, string>();

        foreach (var phrase in SqlClausePhraseCatalog.All)
        {
            var items = phrase.Pattern.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var full = string.Join(" ", items.Select(RenderItem));
            var lead = phrase.Probe.Substring(0, phrase.Probe.LastIndexOf(full, StringComparison.Ordinal));
            var isLead = phrase.After == SqlKeywordPosition.Any;

            for (var index = isLead ? 1 : 0; index < items.Length; index++)
            {
                if (!IsLiteral(items[index]) || (index > 0 && !IsLiteral(items[index - 1])) || (isLead && index < 2))
                {
                    continue;
                }

                var before = lead + string.Concat(items.Take(index).Select(item => RenderItem(item) + " "));
                data.Add(phrase.Pattern, before, items[index]);
            }
        }

        return data;
    }

    /// <summary>
    /// 片語裡的每一個字，在它前面那段都列得出來。
    /// </summary>
    /// <remarks>
    /// 以前寫得出 <c>CREATE OR ALTER</c>，<c>CREATE </c> 之後卻沒有 <c>OR</c>、<c>CREATE OR </c> 之後也沒有
    /// <c>ALTER</c>：剖析器要看到整段才收，逐字探測問不出來。產生器改由整條片語補前面那段，
    /// 範圍與這裡相同——前面那段以字面字結尾，Lead 片語至少兩項，理由見產生器；帶 After 的片語
    /// 第一個字前面那段就是位置，由那個位置的片語或關鍵字目錄給。
    /// </remarks>
    [Theory]
    [MemberData(nameof(PhraseSteps))]
    public void 片語裡的每一個字在前面那段列得出來(string pattern, string textBeforeToken, string word)
    {
        Assert.True(Offered(textBeforeToken).Contains(word), $"{pattern}：{textBeforeToken}| 沒有 {word}");
    }

    private static bool IsLiteral(string item)
    {
        return char.IsLetter(item[0]) || item[0] == '_';
    }

    /// <summary>與產生器的 <c>Get-PhraseProbe</c> 同一套代換。</summary>
    private static string RenderItem(string item)
    {
        return item switch
        {
            "{name}" => "t",
            "{value}" => "1",
            "()" => "(a)",
            "(*" => "(",
            _ => item,
        };
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
