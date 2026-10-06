using System;
using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Snippets;
using SqlAssist.Core.Tests.Completion;
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
    /// 附加的字會被那個片語蓋掉而永遠不出現。那一格同時接得上別的附加片語時，比對回的是聯集，
    /// 所以驗的是字都在，不是同一個物件。
    /// </remarks>
    [Theory]
    [MemberData(nameof(AdditiveProbes))]
    public void 附加片語的探測文字比對回自己而且只是可能(string probe)
    {
        var additive = SqlClausePhraseCatalog.All.Single(phrase => phrase.IsAdditive && phrase.Probe == probe);
        var match = SqlKeywordPositionAnalyzer.Analyze(probe).Phrase;

        Assert.NotNull(match);
        Assert.False(match!.IsCertain);
        Assert.True(match.Phrase.IsAdditive);
        Assert.All(additive.Words, word => Assert.Contains(word, match.Phrase.Words));
    }

    /// <summary>
    /// 附加片語的字在自己那一格只由它列出一次。
    /// </summary>
    /// <remarks>
    /// 別的來源（關鍵字目錄、函式目錄、別的片語）已經列得出的字，照目標過濾時被濾掉，判不出位置時重複：
    /// <c>FROM </c> 之後的 <c>VECTOR_SEARCH</c> 由函式目錄列。<c>None</c> 只在判不出位置時比對得上，
    /// 那時每一個附加片語都算，所以它的字也不在別的附加片語裡。
    /// </remarks>
    [Theory]
    [MemberData(nameof(AdditiveProbes))]
    public void 附加片語的字只由它列出(string probe)
    {
        var additive = SqlClausePhraseCatalog.All.Single(phrase => phrase.IsAdditive && phrase.Probe == probe);
        var context = SqlCompletionContextAnalyzer.Analyze(probe);
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(context.ClausePhrase?.Suggestions ?? Enumerable.Empty<SqlSuggestion>());
        var offered = SuggestionContextFilter.Filter(candidates, context)
            .Where(suggestion => suggestion.Kind is SuggestionKind.Keyword or SuggestionKind.BuiltInFunction)
            .ToArray();

        foreach (var word in additive.Words)
        {
            var source = Assert.Single(offered, suggestion => string.Equals(suggestion.DisplayText, word, StringComparison.OrdinalIgnoreCase));
            Assert.True(source.Tag is SqlClausePhrase { IsAdditive: true }, $"{probe}| 的 {word} 不是附加片語列的");

            if (additive.After == SqlKeywordPosition.None)
            {
                Assert.DoesNotContain(SqlClausePhraseCatalog.All, phrase => phrase.IsAdditive && !ReferenceEquals(phrase, additive) && phrase.Words.Contains(word));
            }
        }
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
    [InlineData("SELECT * FROM OPENROWSET(", "BULK")]
    [InlineData("SELECT * FROM OPENROWSET(BULK 'Copy.csv', ", "FORMAT", "DATA_SOURCE", "FIRSTROW", "SINGLE_CLOB")]
    [InlineData("SET LANGUAGE 'us_english', ", "DATEFORMAT", "DATEFIRST")]
    [InlineData("SET LANGUAGE 'us_english', DATEFORMAT ", "dmy", "ymd")]
    [InlineData("ALTER DATABASE Lib ADD FILE (NAME = Lib2), (NAME = Lib3), (", "NAME", "FILENAME", "SIZE")]
    [InlineData("ALTER DATABASE Lib ADD FILE (NAME = Lib2, SIZE = 5 ", "MB", "GB")]
    [InlineData("CREATE DATABASE Lib ON PRIMARY (NAME = Lib), FILEGROUP LibFg (NAME = Lib2), (NAME = Lib3, ", "MAXSIZE", "FILEGROWTH")]
    [InlineData("CREATE DATABASE Lib ON (NAME = Lib) LOG ON (NAME = LibLog, MAXSIZE = ", "UNLIMITED")]
    [InlineData("ALTER INDEX IX_Loan ON dbo.Loan RESUME WITH (MAXDOP = 2, MAX_DURATION = 5 ", "MINUTES")]
    [InlineData("SELECT * FROM OPENROWSET(BULK 'Copy.csv', FORMAT = 'CSV', ", "DATA_SOURCE", "FIELDQUOTE")]
    [InlineData("CREATE EXTERNAL TABLE dbo.LoanArchiveFile (LoanId int)\nWITH (LOCATION = '/loan/', ", "DATA_SOURCE", "FILE_FORMAT")]
    [InlineData("CREATE AGGREGATE dbo.LoanConcat (@copyNo int) ", "RETURNS")]
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
    [InlineData("CREATE TABLE t (CONNECTION (dbo.Lib_Reader ", "TO")]
    [InlineData("CREATE TABLE t (CONNECTION (dbo.Lib_Reader TO dbo.Copy, dbo.Copy ", "TO")]
    [InlineData("CREATE TABLE t (CONNECTION (dbo.Lib_Reader TO dbo.Copy) ON ", "DELETE")]
    [InlineData("CREATE TABLE t (CONNECTION (dbo.Lib_Reader TO dbo.Copy) ON DELETE CASCADE, CONSTRAINT c CONNECTION (dbo.Copy ", "TO")]
    [InlineData("CREATE TABLE t (CONNECTION (dbo.Lib_Reader TO dbo.Copy) ON DELETE ", "CASCADE", "NO")]
    [InlineData("CREATE TABLE t (a int) AS ", "NODE", "EDGE")]
    [InlineData("CREATE TABLE t AS ", "EDGE")]
    [InlineData("SELECT STRING_AGG(a, ',') WITHIN GROUP (", "GRAPH PATH", "ORDER BY")]
    [InlineData("SELECT STRING_AGG(a, ',') WITHIN GROUP (GRAPH ", "PATH")]
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
    [InlineData("CREATE TABLE t (a int IDENTITY(1, 1) NOT FOR ", "REPLICATION")]
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
    [InlineData("FETCH ABSOLUTE 1 ", "FROM")]
    [InlineData("FETCH ABSOLUTE 1 FROM ", "GLOBAL")]
    [InlineData("FETCH RELATIVE @n FROM ", "GLOBAL")]
    [InlineData("DBCC ", "CHECKDB", "SHOW_STATISTICS", "SQLPERF", "CHECKTABLE", "TRACEON", "FREEPROCCACHE")]
    [InlineData("BEGIN\n    DBCC ", "CHECKIDENT")]
    [InlineData("DBCC CHECKDB ", "WITH")]
    [InlineData("DBCC CHECKIDENT ('dbo.Lib_Tag', RESEED, 100) ", "WITH")]
    [InlineData("DBCC CHECKDB WITH ", "NO_INFOMSGS", "ALL_ERRORMSGS", "TABLOCK", "PHYSICAL_ONLY")]
    [InlineData("DBCC CHECKDB (N'LibArchive') WITH NO_INFOMSGS, ", "ALL_ERRORMSGS", "DATA_PURITY")]
    [InlineData("DBCC SHOW_STATISTICS ('dbo.Loan', st_CopyNo) WITH ", "STAT_HEADER", "DENSITY_VECTOR", "HISTOGRAM")]
    [InlineData("CREATE LOGIN LibLogin WITH ", "PASSWORD")]
    [InlineData("CREATE LOGIN LibLogin WITH PASSWORD = 'x', ", "SID", "DEFAULT_DATABASE", "DEFAULT_LANGUAGE", "CHECK_POLICY", "CHECK_EXPIRATION")]
    [InlineData("CREATE LOGIN LibLogin WITH PASSWORD = 'x' MUST_CHANGE, CHECK_EXPIRATION = ON, ", "CHECK_POLICY", "DEFAULT_LANGUAGE")]
    [InlineData("CREATE LOGIN LibLogin FROM WINDOWS WITH ", "DEFAULT_DATABASE", "DEFAULT_LANGUAGE")]
    [InlineData("ALTER LOGIN LibLogin WITH ", "PASSWORD", "NAME", "DEFAULT_LANGUAGE", "CHECK_POLICY")]
    [InlineData("ALTER LOGIN LibLogin WITH NAME = LibLogin2, ", "DEFAULT_DATABASE", "DEFAULT_LANGUAGE")]
    [InlineData("CREATE USER LibUser ", "WITH", "FOR", "WITHOUT")]
    [InlineData("CREATE USER LibUser WITH ", "PASSWORD", "DEFAULT_SCHEMA", "DEFAULT_LANGUAGE")]
    [InlineData("CREATE USER LibUser WITH PASSWORD = 'x', ", "DEFAULT_SCHEMA", "DEFAULT_LANGUAGE")]
    [InlineData("CREATE USER LibUser\n    WITH PASSWORD = 'x',\n        ", "DEFAULT_SCHEMA")]
    [InlineData("CREATE USER LibUser FOR LOGIN LibLogin WITH ", "DEFAULT_SCHEMA")]
    [InlineData("CREATE USER LibUser WITHOUT LOGIN WITH ", "DEFAULT_SCHEMA")]
    [InlineData("ALTER USER LibUser WITH NAME = LibUser2, ", "DEFAULT_SCHEMA", "LOGIN")]
    [InlineData("CREATE APPLICATION ROLE LibAppRole WITH ", "PASSWORD", "DEFAULT_SCHEMA")]
    [InlineData("CREATE APPLICATION ROLE LibAppRole WITH PASSWORD = 'x', ", "DEFAULT_SCHEMA")]
    [InlineData("ALTER APPLICATION ROLE LibAppRole WITH ", "NAME", "PASSWORD", "DEFAULT_SCHEMA")]
    [InlineData("ALTER APPLICATION ROLE LibAppRole WITH NAME = LibAppRole2, ", "PASSWORD", "DEFAULT_SCHEMA")]
    [InlineData("RAISERROR ('x', 16, 1) WITH ", "LOG", "NOWAIT", "SETERROR")]
    [InlineData("RAISERROR (@msg, 16, 1, @CopyNo) WITH NOWAIT, ", "LOG", "SETERROR")]
    [InlineData("SELECT a FROM t FOR XML AUTO, ", "TYPE", "ROOT", "ELEMENTS")]
    [InlineData("EXEC dbo.usp_Renew @CopyNo = 1, @Due = @d OUTPUT WITH ", "RECOMPILE", "RESULT")]
    [InlineData("EXECUTE dbo.usp_Copies WITH RECOMPILE, ", "RESULT")]
    [InlineData("EXEC dbo.usp_Copies WITH RECOMPILE,\n    RESULT ", "SETS")]
    [InlineData("BACKUP LOG LibArchive TO DISK = 'x' WITH ", "INIT", "COMPRESSION")]
    [InlineData("RESTORE DATABASE LibArchive FROM DISK = 'x' WITH ", "FILE", "REPLACE", "NORECOVERY")]
    [InlineData("RESTORE LOG LibArchive FROM DISK = 'x' WITH ", "NORECOVERY")]
    [InlineData("SELECT STRING_AGG(Title, ', ') ", "WITHIN", "OVER", "AT")]
    [InlineData("SELECT Branch, STRING_AGG(Title, ', ') WITHIN ", "GROUP")]
    [InlineData("SELECT PERCENTILE_CONT(0.5) WITHIN GROUP (", "ORDER")]
    [InlineData("SELECT PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY Fee) ", "OVER")]
    [InlineData("SELECT Branch FROM dbo.Copy ORDER BY STRING_AGG(Title, ', ') ", "WITHIN")]
    [InlineData("PRINT @a ", "AT", "VECTOR_SEARCH")]
    [InlineData("DELETE FROM dbo.Loan WHERE ", "CURRENT")]
    [InlineData("DELETE FROM dbo.Loan WHERE CURRENT ", "OF")]
    [InlineData("UPDATE dbo.Loan SET Fee = 0 WHERE CURRENT ", "OF")]
    [InlineData("DELETE FROM dbo.Loan WHERE CURRENT OF ", "GLOBAL")]
    [InlineData("DBCC CHECKIDENT ('dbo.Lib_Tag', ", "NORESEED", "RESEED")]
    [InlineData("DBCC CHECKDB (N'LibArchive', ", "NOINDEX", "REPAIR_ALLOW_DATA_LOSS", "REPAIR_FAST", "REPAIR_REBUILD")]
    [InlineData("DBCC SQLPERF (", "LOGSPACE")]
    [InlineData("DBCC SQLPERF ('sys.dm_os_wait_stats', ", "CLEAR")]
    [InlineData("DBCC SHRINKFILE (LibArchive_log, ", "EMPTYFILE", "NOTRUNCATE", "TRUNCATEONLY")]
    [InlineData("DBCC CHECKTABLE ('dbo.Loan', ", "NOINDEX", "REPAIR_REBUILD")]
    [InlineData("SELECT JSON_OBJECT('Title': Title ", "NULL ON NULL", "ABSENT ON NULL", "RETURNING JSON")]
    [InlineData("SELECT JSON_OBJECT('Title': UPPER(Title) + 'x' ABSENT ", "ON NULL")]
    [InlineData("SELECT JSON_ARRAY(1, @n * 2 NULL ON NULL ", "RETURNING JSON")]
    [InlineData("SELECT JSON_ARRAY(", "NULL", "ABSENT")]
    [InlineData("SELECT JSON_OBJECT(NULL ", "ON NULL")]
    [InlineData("SELECT JSON_ARRAYAGG(CopyNo ORDER BY DueDate ", "NULL ON NULL", "RETURNING JSON")]
    [InlineData("SELECT JSON_OBJECTAGG(CopyNo: DueDate RETURNING ", "JSON")]
    [InlineData("SELECT JSON_VALUE(@j, '$.CopyNo' ", "RETURNING")]
    [InlineData("SELECT * FROM dbo.Loan WHERE JSON_VALUE(Note, '$.Due' ", "RETURNING")]
    [InlineData("SELECT * FROM dbo.Loan WHERE DueDate AT TIME ZONE @Zone ", "AT")]
    [InlineData("SELECT * FROM dbo.Loan WHERE @From < DueDate ", "AT")]
    [InlineData("SELECT * FROM dbo.Loan ORDER BY DueDate ", "AT", "DESC")]
    [InlineData("UPDATE dbo.Loan SET DueDate = @Due ", "AT", "WHERE")]
    [InlineData("CREATE TABLE t (Due datetimeoffset NOT NULL DEFAULT @Due ", "AT", "CONSTRAINT")]
    [InlineData("DECLARE @t TABLE (Due datetimeoffset DEFAULT @Due AT TIME ZONE @Zone ", "AT", "NOT")]
    [InlineData("SELECT dbo.fn_Agg(ALL CopyNo, 1) ", "OVER")]
    [InlineData("SELECT TRY_PARSE('2026-10-06' AS date ", "USING")]
    [InlineData("SELECT PARSE(@s AS datetime2 ", "USING")]
    [InlineData("DECLARE @x xml(", "CONTENT", "DOCUMENT")]
    [InlineData("CREATE TABLE t (Note [xml](", "CONTENT", "DOCUMENT")]
    [InlineData("CREATE PROCEDURE dbo.usp_Renew @Note sys.xml(", "CONTENT")]
    [InlineData("CREATE PROCEDURE dbo.usp_Renew @Copies dbo.CopyList ", "READONLY")]
    [InlineData("CREATE FUNCTION dbo.fn_Fee (@Copies dbo.CopyList READONLY, @Loans dbo.LoanList ", "READONLY")]
    [InlineData("CREATE PROCEDURE dbo.usp_Renew @Copies dbo.CopyList READONLY AS ", "SELECT", "EXTERNAL")]
    [InlineData("MERGE TOP (10) ", "PERCENT", "INTO")]
    [InlineData("MERGE TOP (10) PERCENT ", "INTO")]
    [InlineData("UPDATE TOP (10) ", "PERCENT")]
    [InlineData("DELETE TOP (10) ", "PERCENT", "FROM")]
    [InlineData("INSERT TOP (10) ", "PERCENT", "INTO")]
    [InlineData("DECLARE @t TABLE (DueDate datetime2, Due AS DueDate ", "AT", "PERSISTED")]
    [InlineData("SELECT NEXT VALUE FOR dbo.LoanSeq ", "OVER", "AT", "FROM")]
    [InlineData("SELECT LAG(Fee) ", "IGNORE", "RESPECT", "OVER")]
    [InlineData("SELECT LAG(Fee) IGNORE ", "NULLS")]
    [InlineData("SELECT FIRST_VALUE(Fee) RESPECT NULLS ", "OVER")]
    [InlineData("SELECT SUM(a) OVER (ORDER BY a ROWS ", "BETWEEN", "UNBOUNDED", "CURRENT")]
    [InlineData("SELECT SUM(a) OVER (ORDER BY a ROWS 2 ", "PRECEDING")]
    [InlineData("SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN 2 ", "PRECEDING AND", "FOLLOWING AND")]
    [InlineData("SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN CURRENT ROW ", "AND")]
    [InlineData("SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN CURRENT ROW AND ", "UNBOUNDED", "CURRENT")]
    [InlineData("SELECT SUM(a) OVER (ORDER BY a ROWS BETWEEN 1 FOLLOWING AND @n + 1 ", "FOLLOWING")]
    [InlineData("SELECT SUM(a) OVER (w RANGE BETWEEN UNBOUNDED PRECEDING AND ", "UNBOUNDED", "CURRENT")]
    [InlineData("SELECT a FROM t FOR SYSTEM_TIME BETWEEN @From ", "AND")]
    [InlineData("SELECT a FROM t FOR SYSTEM_TIME FROM '2026-01-01' ", "TO")]
    [InlineData("SELECT a FROM t ORDER BY a FETCH ", "APPROX", "APPROXIMATE")]
    [InlineData("SELECT a FROM t ORDER BY a FETCH APPROXIMATE ", "NEXT", "FIRST")]
    [InlineData("SELECT a FROM t ORDER BY a FETCH APPROX FIRST 10 ", "ROWS ONLY", "ROW ONLY")]
    [InlineData("SELECT a FROM t ORDER BY a OFFSET @a - 1 ROWS FETCH NEXT @a - @b + 1 ROWS ", "ONLY")]
    [InlineData("SELECT * FROM PREDICT(", "MODEL")]
    [InlineData("SELECT * FROM PREDICT(MODEL = @Model, ", "DATA")]
    [InlineData("SELECT * FROM PREDICT(MODEL = @Model, DATA = dbo.Copy AS d, RUNTIME = ", "ONNX")]
    [InlineData("CREATE TABLE Shelf (Code char(10) ", "HIDDEN", "MASKED", "SPARSE", "COLLATE", "NOT", "NULL", "CONSTRAINT")]
    [InlineData("CREATE TABLE Shelf (Code char(10) COLLATE Latin1_General_CI_AS SPARSE ", "MASKED", "NULL")]
    [InlineData("CREATE TABLE Shelf (Fee int MASKED WITH (", "FUNCTION")]
    [InlineData("CREATE TABLE Shelf (Fee int MASKED WITH (FUNCTION = 'default()') ", "HIDDEN", "NOT")]
    [InlineData("CREATE TABLE Shelf (Doc varbinary(max) ", "FILESTREAM")]
    [InlineData("CREATE TABLE Shelf (Doc xml ", "COLUMN_SET")]
    [InlineData("CREATE TABLE Shelf (Fee int, Neg AS -Fee ", "PERSISTED")]
    [InlineData("CREATE TABLE Shelf (Fee int IDENTITY(1, 1) NOT ", "FOR", "NULL")]
    [InlineData("CREATE TABLE Shelf (Fee int CONSTRAINT DfFee ", "DEFAULT", "PRIMARY")]
    [InlineData("DECLARE @Shelf AS TABLE (Code int ", "SPARSE")]
    [InlineData("ALTER TABLE Shelf ADD Doc xml ", "COLUMN_SET")]
    [InlineData("ALTER TABLE Shelf ALTER COLUMN Code nvarchar(10) ", "MASKED", "HIDDEN")]
    [InlineData("SELECT * FROM OPENJSON(@j) WITH (Code nvarchar(10) ", "COLLATE", "AS")]
    [InlineData("SELECT * FROM OPENJSON(@j) WITH (Tags nvarchar(max) '$.tags' ", "AS")]
    [InlineData("CREATE ", "JSON", "SELECTIVE", "XML")]
    [InlineData("CREATE XML INDEX XIX_Copy ON dbo.Copy (Note) ", "USING")]
    [InlineData("CREATE XML INDEX XIX_Copy ON dbo.Copy (Note) USING ", "XML")]
    [InlineData("CREATE XML INDEX XIX_Copy ON dbo.Copy (Note) USING XML INDEX PXIX_Copy FOR ", "PATH", "VALUE", "PROPERTY")]
    [InlineData("CREATE SELECTIVE XML INDEX SXIX_Copy ON dbo.Copy (Note) ", "FOR", "WITH")]
    [InlineData("CREATE SELECTIVE XML INDEX SXIX_Copy ON dbo.Copy (Note) WITH XMLNAMESPACES ('urn:lib' AS Lib) FOR (BookTitle = '/a' AS ", "SQL", "XQUERY")]
    [InlineData("CREATE JSON INDEX JIX_Copy ON dbo.Copy (Doc) ", "FOR", "WITH")]
    [InlineData("CREATE TYPE dbo.CopyList AS TABLE (CopyNo int) WITH (", "MEMORY_OPTIMIZED")]
    [InlineData("CREATE TABLE Shelf (Code int, INDEX IX_Shelf NONCLUSTERED (Code) ", "INCLUDE", "WHERE", "WITH")]
    [InlineData("CREATE TABLE Shelf (Code int PRIMARY KEY NONCLUSTERED ", "HASH", "NOT", "WITH")]
    [InlineData("ALTER TABLE Shelf ADD CONSTRAINT UqShelf UNIQUE NONCLUSTERED ", "HASH")]
    [InlineData("ALTER TABLE Shelf ALTER INDEX IX_Shelf ", "REBUILD")]
    [InlineData("ALTER TABLE Shelf ALTER INDEX IX_Shelf REBUILD WITH (", "BUCKET_COUNT")]
    [InlineData("CREATE TABLE Shelf (Code int) WITH (XML_COMPRESSION = ON ", "ON")]
    [InlineData("CREATE FULLTEXT INDEX ON dbo.Copy (Title ", "TYPE", "LANGUAGE", "STATISTICAL_SEMANTICS")]
    [InlineData("CREATE FULLTEXT INDEX ON dbo.Copy (Title TYPE COLUMN Ext ", "LANGUAGE", "STATISTICAL_SEMANTICS")]
    [InlineData("ALTER FULLTEXT INDEX ON dbo.Copy ADD (Title, Summary ", "TYPE", "LANGUAGE")]
    [InlineData("ALTER FULLTEXT INDEX ON dbo.Copy ADD (Title) WITH ", "NO")]
    [InlineData("ALTER FULLTEXT INDEX ON dbo.Copy START ", "FULL", "INCREMENTAL", "UPDATE")]
    [InlineData("ALTER FULLTEXT INDEX ON dbo.Copy START INCREMENTAL ", "POPULATION")]
    [InlineData("ALTER FULLTEXT INDEX ON dbo.Copy STOP ", "POPULATION")]
    [InlineData("ALTER FULLTEXT INDEX ON dbo.Copy SET STOPLIST = ", "SYSTEM", "OFF")]
    [InlineData("CREATE FULLTEXT INDEX ON dbo.Copy KEY INDEX PK_Copy WITH ", "CHANGE_TRACKING", "STOPLIST")]
    [InlineData("CREATE FULLTEXT INDEX ON dbo.Copy (Title) KEY INDEX PK_Copy ON CopyCatalog WITH ", "CHANGE_TRACKING")]
    [InlineData("CREATE FULLTEXT INDEX ON dbo.Copy (Title) KEY INDEX PK_Copy ON CopyCatalog WITH CHANGE_TRACKING = ", "MANUAL", "AUTO", "OFF")]
    [InlineData("CREATE FULLTEXT INDEX ON dbo.Copy (Title) KEY INDEX PK_Copy WITH STOPLIST ", "SYSTEM", "OFF")]
    [InlineData("CREATE FULLTEXT INDEX ON dbo.Copy KEY INDEX PK_Copy WITH (STOPLIST = SYSTEM, CHANGE_TRACKING = OFF, NO ", "POPULATION")]
    [InlineData("CREATE FULLTEXT INDEX ON dbo.Copy (Title) KEY INDEX PK_Copy WITH SEARCH PROPERTY LIST = CopyProps, CHANGE_TRACKING OFF, NO ", "POPULATION")]
    [InlineData("ALTER INDEX SXIX_Copy ON dbo.Copy FOR (REMOVE BookYear, ADD BookTitle = '/a' AS ", "SQL", "XQUERY")]
    [InlineData("ALTER INDEX SXIX_Copy ON dbo.Copy FOR (ADD BookTitle = '/a' AS XQUERY 'xs:string' ", "MAXLENGTH", "SINGLETON")]
    [InlineData("CREATE SELECTIVE XML INDEX SXIX_Copy ON dbo.Copy (Note) FOR (BookTitle = '/a' AS SQL nvarchar(20) ", "SINGLETON")]
    [InlineData("CREATE SELECTIVE XML INDEX SXIX_Copy ON dbo.Copy (Note) FOR (BookTitle = '/a' AS XQUERY 'xs:string' MAXLENGTH(20) ", "SINGLETON")]
    [InlineData("ALTER INDEX SXIX_Copy ON dbo.Copy FOR (ADD BookTitle = '/a' AS SQL nvarchar(20) ", "SINGLETON")]
    [InlineData("ALTER FULLTEXT ", "CATALOG", "INDEX", "STOPLIST")]
    [InlineData("ALTER FULLTEXT CATALOG CopyCatalog ", "REBUILD", "REORGANIZE")]
    [InlineData("ALTER FULLTEXT CATALOG CopyCatalog REBUILD WITH ACCENT_SENSITIVITY = ON ALTER FULLTEXT ", "CATALOG")]
    [InlineData("ALTER FULLTEXT STOPLIST CopyStoplist ADD 'the' ", "LANGUAGE")]
    [InlineData("ALTER FULLTEXT STOPLIST CopyStoplist DROP ", "ALL")]
    [InlineData("ALTER FULLTEXT STOPLIST CopyStoplist DROP ALL ", "LANGUAGE")]
    [InlineData("CREATE FULLTEXT STOPLIST CopyStoplist FROM ", "SYSTEM")]
    [InlineData("CREATE FULLTEXT STOPLIST CopyStoplist FROM SYSTEM ", "STOPLIST")]
    [InlineData("CREATE FULLTEXT CATALOG CopyCatalog ", "WITH", "ON", "IN", "AS")]
    [InlineData("CREATE FULLTEXT CATALOG CopyCatalog WITH ", "ACCENT_SENSITIVITY")]
    [InlineData("CREATE FULLTEXT CATALOG CopyCatalog ON FILEGROUP LibFs IN ", "PATH")]
    [InlineData("CREATE FULLTEXT CATALOG CopyCatalog IN PATH 'x' WITH ", "ACCENT_SENSITIVITY")]
    [InlineData("SELECT CopyNo FROM dbo.Copy WHERE CONTAINS(", "PROPERTY")]
    [InlineData("SELECT * FROM CONTAINSTABLE(dbo.Copy, (Title, Summary), 'book', ", "LANGUAGE")]
    [InlineData("SELECT * FROM dbo.Copy WHERE FREETEXT(Title, 'book', ", "LANGUAGE")]
    [InlineData("BEGIN DIALOG @Handle ", "FROM")]
    [InlineData("BEGIN DIALOG CONVERSATION @Handle FROM ", "SERVICE")]
    [InlineData("BEGIN DIALOG @Handle FROM SERVICE LoanSender TO ", "SERVICE")]
    [InlineData("BEGIN DIALOG @Handle FROM SERVICE LoanSender TO SERVICE 'LoanReceiver' ", "ON", "WITH")]
    [InlineData("BEGIN DIALOG @Handle FROM SERVICE LoanSender TO SERVICE 'LoanReceiver' ON ", "CONTRACT")]
    [InlineData("CREATE SERVICE LoanService ON ", "QUEUE")]
    [InlineData("CREATE SERVICE LoanService AUTHORIZATION dbo ", "ON")]
    [InlineData("CREATE MESSAGE TYPE LoanRequest VALIDATION = ", "WELL_FORMED_XML", "NONE")]
    [InlineData("CREATE MESSAGE TYPE LoanRequest\n    ", "VALIDATION")]
    [InlineData("BEGIN DIALOG @Handle FROM SERVICE LoanSender TO SERVICE 'LoanReceiver', 'current database' ", "ON", "WITH")]
    [InlineData("WAITFOR (GET CONVERSATION GROUP @Group FROM LoanQueue), ", "TIMEOUT")]
    [InlineData("CREATE ENDPOINT LoanBroker AS TCP (LISTENER_PORT = 4022) FOR SERVICE_BROKER (ENCRYPTION = ", "REQUIRED", "DISABLED")]
    [InlineData("CREATE REMOTE SERVICE BINDING LoanBinding TO ", "SERVICE")]
    [InlineData("WAITFOR (", "RECEIVE", "GET")]
    [InlineData("", "RECEIVE", "SEND")]
    [InlineData("SEND ON CONVERSATION @handle MESSAGE TYPE LoanRequest (@body)\n", "SEND", "RECEIVE")]
    [InlineData("SEND ", "ON CONVERSATION")]
    [InlineData("SEND ON CONVERSATION @handle ", "MESSAGE TYPE")]
    [InlineData("SEND ON CONVERSATION (@handle, @other) MESSAGE ", "TYPE")]
    [InlineData("", "MOVE", "GET")]
    [InlineData("MOVE CONVERSATION @handle ", "TO")]
    [InlineData("CREATE BROKER PRIORITY LoanPriority FOR ", "CONVERSATION")]
    [InlineData("ALTER BROKER PRIORITY LoanPriority FOR CONVERSATION SET (PRIORITY_LEVEL = 5, ", "CONTRACT_NAME", "REMOTE_SERVICE_NAME")]
    [InlineData("CREATE QUEUE LoanQueue ", "WITH", "ON")]
    [InlineData("CREATE QUEUE dbo.LoanQueue WITH STATUS = ON, ", "ACTIVATION", "RETENTION")]
    [InlineData("CREATE QUEUE LoanQueue WITH ACTIVATION (STATUS = ON, ", "PROCEDURE_NAME", "MAX_QUEUE_READERS")]
    [InlineData("CREATE CONTRACT LoanContract (LoanRequest ", "SENT BY")]
    [InlineData("CREATE CONTRACT LoanContract (LoanRequest SENT BY ", "TARGET", "INITIATOR", "ANY")]
    [InlineData("CREATE CONTRACT LoanContract AUTHORIZATION dbo (LoanRequest SENT BY ANY, LoanReply SENT BY ", "TARGET")]
    [InlineData("WAITFOR (RECEIVE * FROM LoanQueue), ", "TIMEOUT")]
    [InlineData("ALTER QUEUE LoanQueue WITH ACTIVATION (", "DROP", "STATUS")]
    [InlineData("ALTER SERVICE LoanService ", "ON QUEUE")]
    [InlineData("ALTER SERVICE LoanService (", "ADD", "DROP")]
    [InlineData("ALTER SERVICE LoanService ON QUEUE dbo.LoanQueue (ADD CONTRACT LoanContract, DROP ", "CONTRACT")]
    [InlineData("ALTER SERVICE LoanService (ADD ", "CONTRACT")]
    [InlineData("ALTER ROUTE LoanRoute WITH SERVICE_NAME = 'LoanService', ", "LIFETIME", "ADDRESS", "MIRROR_ADDRESS")]
    [InlineData("CREATE ROUTE LoanRoute AUTHORIZATION dbo WITH ", "SERVICE_NAME", "BROKER_INSTANCE")]
    [InlineData("ALTER REMOTE SERVICE BINDING LoanBinding WITH USER = LoanUser, ", "ANONYMOUS")]
    [InlineData("CREATE REMOTE SERVICE BINDING LoanBinding TO SERVICE 'LoanService' WITH ANONYMOUS = ON, ", "USER")]
    [InlineData("ALTER EVENT SESSION LoanTrace ON SERVER DROP EVENT sqlserver.rpc_completed, ", "DROP")]
    [InlineData("ALTER EVENT SESSION LoanTrace ON SERVER DROP TARGET package0.ring_buffer, DROP ", "TARGET")]
    [InlineData("CREATE EVENT SESSION LoanTrace ON SERVER ADD EVENT sqlserver.rpc_completed (SET collect_statement = 1 ", "ACTION", "WHERE")]
    [InlineData("CREATE EVENT SESSION LoanTrace ON SERVER ADD EVENT sqlserver.rpc_completed (SET collect_statement = 1, collect_data_stream = 0 ", "ACTION")]
    [InlineData("CREATE ENDPOINT LoanBroker AS TCP (LISTENER_PORT = 4022) FOR SERVICE_BROKER (", "ENCRYPTION", "AUTHENTICATION")]
    [InlineData("CREATE ENDPOINT LoanMirror STATE = STARTED AS TCP (LISTENER_PORT = 5022) FOR DATABASE_MIRRORING (ROLE = PARTNER, ", "ENCRYPTION")]
    [InlineData("ALTER ENDPOINT LoanBroker FOR SERVICE_BROKER (AUTHENTICATION = ", "WINDOWS", "CERTIFICATE")]
    [InlineData("CREATE ENDPOINT LoanBroker AS TCP (LISTENER_PORT = 4022) FOR SERVICE_BROKER (ENCRYPTION = REQUIRED ", "ALGORITHM")]
    [InlineData("CREATE INDEX IX_Loan ON dbo.Loan (CopyNo) WITH (ONLINE = ", "ON", "OFF")]
    [InlineData("ALTER TABLE dbo.Loan SET (SYSTEM_VERSIONING = ON (HISTORY_RETENTION_PERIOD = ", "INFINITE")]
    [InlineData("ALTER DATABASE Lib SET AUTO_CREATE_STATISTICS ON (", "INCREMENTAL")]
    [InlineData("ALTER DATABASE Lib SET READ_ONLY, AUTO_CREATE_STATISTICS ON (", "INCREMENTAL")]
    [InlineData("ALTER DATABASE Lib SET READ_ONLY, QUERY_STORE (", "OPERATION_MODE")]
    [InlineData("ALTER EVENT SESSION LoanTrace ON SERVER WITH (", "MEMORY_PARTITION_MODE", "STARTUP_STATE")]
    [InlineData("ALTER EVENT SESSION LoanTrace ON DATABASE WITH (MEMORY_PARTITION_MODE = ", "PER_CPU", "NONE")]
    [InlineData("ALTER SERVER AUDIT LibAudit TO APPLICATION_LOG WITH (", "QUEUE_DELAY", "ON_FAILURE")]
    [InlineData("CREATE SERVER AUDIT LibAudit TO FILE (FILEPATH = 'x') WITH (", "QUEUE_DELAY")]
    [InlineData("CREATE SERVER AUDIT LibAudit TO ", "URL", "FILE")]
    [InlineData("ALTER SERVER AUDIT LibAudit ", "MODIFY", "REMOVE WHERE", "WITH")]
    [InlineData("ALTER SERVER AUDIT LibAudit MODIFY ", "NAME")]
    [InlineData("KILL ", "QUERY NOTIFICATION SUBSCRIPTION", "STATS JOB")]
    [InlineData("KILL QUERY NOTIFICATION SUBSCRIPTION ", "ALL")]
    [InlineData("CREATE TYPE dbo.LibPoint ", "EXTERNAL NAME", "FROM")]
    [InlineData("CREATE TYPE dbo.LibPoint EXTERNAL ", "NAME")]
    [InlineData("TRUNCATE TABLE dbo.Loan WITH (", "PARTITIONS")]
    [InlineData("BACKUP DATABASE LibArchive FILEGROUP = 'a', ", "FILEGROUP", "FILE")]
    [InlineData("BACKUP DATABASE LibArchive FILE = 'a', FILEGROUP = 'b' TO ", "DISK", "URL")]
    [InlineData("BACKUP DATABASE @db FILE = 'a', ", "FILEGROUP")]
    [InlineData("ALTER DATABASE Lib SET SINGLE_USER ", "WITH")]
    [InlineData("ALTER DATABASE Lib SET READ_ONLY, ", "RESTRICTED_USER", "AUTO_CLOSE")]
    [InlineData("ALTER DATABASE Lib SET READ_ONLY, RESTRICTED_USER ", "WITH")]
    [InlineData("ALTER DATABASE Lib SET READ_COMMITTED_SNAPSHOT OFF ", "WITH")]
    [InlineData("ALTER DATABASE Lib SET OFFLINE WITH ", "ROLLBACK", "NO_WAIT")]
    [InlineData("ALTER DATABASE CURRENT SET MULTI_USER WITH ROLLBACK ", "AFTER", "IMMEDIATE")]
    [InlineData("ALTER DATABASE Lib SET SINGLE_USER WITH ROLLBACK AFTER 10 ", "SECONDS")]
    [InlineData("ALTER DATABASE Lib MODIFY FILEGROUP LibFg ", "READ_ONLY", "READ_WRITE", "DEFAULT", "AUTOGROW_ALL_FILES", "NAME")]
    [InlineData("ALTER DATABASE Lib ADD FILEGROUP LibFs ", "CONTAINS")]
    [InlineData("ALTER DATABASE Lib SET AUTOMATIC_TUNING (", "FORCE_LAST_GOOD_PLAN", "CREATE_INDEX", "DROP_INDEX")]
    [InlineData("ALTER DATABASE Lib SET AUTOMATIC_TUNING (CREATE_INDEX = ", "ON", "OFF", "DEFAULT")]
    [InlineData("CREATE DATABASE Lib ", "WITH", "ON", "COLLATE")]
    [InlineData("CREATE DATABASE Lib ON (NAME = Lib, FILENAME = 'x') ", "WITH", "LOG ON")]
    [InlineData("CREATE DATABASE Lib WITH ", "LEDGER", "FILESTREAM", "TRUSTWORTHY", "DB_CHAINING")]
    [InlineData("CREATE DATABASE Lib ON (NAME = Lib, FILENAME = 'x') LOG ON (NAME = LibLog, FILENAME = 'y') WITH TRUSTWORTHY ON, ", "LEDGER", "FILESTREAM")]
    [InlineData("CREATE DATABASE Lib WITH LEDGER = ", "ON", "OFF")]
    [InlineData("CREATE DATABASE Lib WITH TRUSTWORTHY ON\nCREATE DATABASE Lib2 WITH ", "LEDGER", "FILESTREAM")]
    [InlineData("CREATE DATABASE Lib WITH FILESTREAM (", "NON_TRANSACTED_ACCESS", "DIRECTORY_NAME")]
    [InlineData("CREATE DATABASE Lib WITH LEDGER = ON, FILESTREAM (NON_TRANSACTED_ACCESS = ", "OFF", "FULL")]
    [InlineData("ALTER AVAILABILITY GROUP LibAg ADD REPLICA ON 'LIBSRV2' WITH (", "ENDPOINT_URL", "AVAILABILITY_MODE", "FAILOVER_MODE")]
    [InlineData("ALTER AVAILABILITY GROUP LibAg ADD REPLICA ON 'LIBSRV2' WITH (FAILOVER_MODE = AUTOMATIC),\n'LIBSRV3' WITH (FAILOVER_MODE = ", "MANUAL")]
    [InlineData("ALTER AVAILABILITY GROUP LibAg MODIFY REPLICA ON 'LIBSRV2' WITH (SECONDARY_ROLE (ALLOW_CONNECTIONS = ", "NO", "READ_ONLY", "ALL")]
    [InlineData("CREATE AVAILABILITY GROUP LibAg FOR DATABASE Lib REPLICA ON 'LIBSRV1' WITH (AVAILABILITY_MODE = ", "SYNCHRONOUS_COMMIT", "ASYNCHRONOUS_COMMIT")]
    [InlineData("SELECT c.CopyNo FROM dbo.Copy c INNER ", "JOIN", "LOOP JOIN", "HASH JOIN", "MERGE JOIN", "REMOTE JOIN")]
    [InlineData("SELECT c.CopyNo FROM dbo.Copy c LEFT OUTER ", "JOIN", "HASH JOIN")]
    [InlineData("SELECT c.CopyNo FROM dbo.Copy c FULL ", "OUTER", "JOIN", "LOOP JOIN")]
    [InlineData("SELECT c.CopyNo FROM dbo.Copy c INNER MERGE ", "JOIN")]
    [InlineData("SELECT c.CopyNo FROM dbo.Copy c JOIN dbo.Loan l ON l.CopyNo = c.CopyNo RIGHT ", "OUTER", "REMOTE JOIN")]
    [InlineData("SELECT c.CopyNo FROM dbo.Copy c LEFT JOIN ", "OPENROWSET")]
    [InlineData("SELECT BranchId FROM dbo.Copy GROUP BY CUBE (BranchId), ", "ROLLUP", "CUBE", "GROUPING SETS", "CASE")]
    [InlineData("SELECT BranchId FROM dbo.Copy GROUP BY BranchId, ROLLUP (CopyNo), GROUPING ", "SETS")]
    [InlineData("SELECT BranchId FROM dbo.Copy GROUP BY GROUPING ", "SETS")]
    [InlineData("SELECT CopyNo FROM dbo.Copy WHERE BranchId IS NOT DISTINCT FROM ", "NULL")]
    [InlineData("SELECT CASE WHEN BranchId IS NOT DISTINCT FROM ", "NULL")]
    [InlineData("SELECT * FROM OPENROWSET (BULK 'x', FORMATFILE = 'f', ORDER (CopyNo ", "ASC", "DESC")]
    [InlineData("SELECT * FROM OPENROWSET (BULK 'x', FORMATFILE = 'f', ORDER (CopyNo ASC, BranchId ", "ASC", "DESC")]
    [InlineData("WITH ", "XMLNAMESPACES", "CHANGE_TRACKING_CONTEXT")]
    [InlineData("SELECT 1;\nWITH ", "XMLNAMESPACES")]
    [InlineData("CREATE WORKLOAD GROUP LibGroup USING ", "EXTERNAL")]
    [InlineData("CREATE WORKLOAD GROUP LibGroup WITH (IMPORTANCE = HIGH) USING LibPool, ", "EXTERNAL")]
    [InlineData("ALTER WORKLOAD GROUP LibGroup WITH (IMPORTANCE = LOW) USING ", "EXTERNAL")]
    [InlineData("DROP SYMMETRIC KEY LibKey ", "REMOVE")]
    [InlineData("DROP ASYMMETRIC KEY LibKey REMOVE ", "PROVIDER KEY")]
    [InlineData("DENY SELECT ON EXTERNAL MODEL::LibModel TO LibUser, LibReader\n", "CASCADE")]
    [InlineData("REVOKE SELECT ON dbo.Copy FROM LibUser, LibReader ", "CASCADE")]
    [InlineData("GRANT SELECT ON dbo.Copy TO LibUser, LibReader ", "WITH GRANT OPTION")]
    [InlineData("ALTER INDEX IX_Copy ON dbo.Copy REORGANIZE PARTITION = 1 WITH (", "COMPRESS_ALL_ROW_GROUPS", "LOB_COMPACTION")]
    [InlineData("ALTER TABLE dbo.Copy DROP CONSTRAINT PK_Copy WITH (", "MOVE TO", "ONLINE")]
    [InlineData("ALTER TABLE dbo.Copy DROP COLUMN Shelf, CONSTRAINT PK_Copy WITH (ONLINE = ON), CK_Copy WITH (", "MOVE TO")]
    [InlineData("ALTER EVENT SESSION LibTrace ON DATABASE ", "ADD", "DROP", "STATE")]
    [InlineData("ALTER EVENT SESSION LibTrace ON DATABASE DROP ", "EVENT", "TARGET")]
    [InlineData("CREATE SYMMETRIC KEY LibEkmKey FROM PROVIDER LibEkm WITH ", "PROVIDER_KEY_NAME", "CREATION_DISPOSITION", "ALGORITHM")]
    [InlineData("CREATE SYMMETRIC KEY LibEkmKey FROM PROVIDER LibEkm WITH CREATION_DISPOSITION = ", "CREATE_NEW", "OPEN_EXISTING")]
    [InlineData("CREATE ASYMMETRIC KEY LibEkmAsym FROM PROVIDER LibEkm WITH ALGORITHM = RSA_2048, ", "PROVIDER_KEY_NAME", "CREATION_DISPOSITION")]
    [InlineData("CREATE ASYMMETRIC KEY LibEkmAsym FROM PROVIDER LibEkm WITH ALGORITHM = RSA_2048, CREATION_DISPOSITION = ", "CREATE_NEW", "OPEN_EXISTING")]
    [InlineData("CREATE SYMMETRIC KEY LibEkmKey AUTHORIZATION LibUser\nFROM PROVIDER LibEkm WITH PROVIDER_KEY_NAME = 'x', ", "CREATION_DISPOSITION")]
    [InlineData("CREATE SYMMETRIC KEY LibKey WITH KEY_SOURCE = 'x', IDENTITY_VALUE = 'x', ALGORITHM = ", "AES_256")]
    [InlineData("ADD SIGNATURE TO ", "OBJECT", "ASSEMBLY")]
    [InlineData("ADD COUNTER SIGNATURE TO OBJECT::dbo.usp_Lend BY ", "ASYMMETRIC KEY", "CERTIFICATE")]
    [InlineData("ADD SIGNATURE TO ASYMMETRIC KEY::LibKey BY ", "CERTIFICATE")]
    [InlineData("DROP SIGNATURE FROM OBJECT::dbo.usp_Lend BY ", "CERTIFICATE")]
    [InlineData("ALTER LOGIN LibLogin ", "ADD CREDENTIAL", "DROP CREDENTIAL", "ENABLE", "WITH")]
    [InlineData("ALTER LOGIN LibLogin WITH NAME = LibLogin2, CHECK_POLICY = ", "ON", "OFF")]
    [InlineData("ALTER ASSEMBLY LibClr ", "FROM", "WITH", "DROP FILE", "ADD FILE FROM")]
    [InlineData("ALTER ASSEMBLY LibClr DROP ", "FILE")]
    [InlineData("ALTER ASSEMBLY LibClr DROP FILE ", "ALL")]
    [InlineData("ALTER ASSEMBLY LibClr WITH PERMISSION_SET = SAFE, VISIBILITY = ", "ON", "OFF")]
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
    [InlineData("SET LANGUAGE 'us_english', ", "SELECT")]
    [InlineData("ALTER DATABASE Lib ADD FILE (NAME = Lib2), (", "SELECT")]
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
    [InlineData("DBCC ", "SELECT")]
    [InlineData("DBCC CHECKDB ", "SELECT")]
    [InlineData("DBCC CHECKDB WITH ", "SELECT")]
    [InlineData("DBCC CHECKDB WITH NO_INFOMSGS, ", "RECOMPILE")]
    [InlineData("CREATE LOGIN LibLogin WITH PASSWORD = 'x', ", "SELECT")]
    [InlineData("ALTER USER LibUser WITH NAME = LibUser2, ", "SELECT")]
    [InlineData("RAISERROR ('x', 16, 1) WITH NOWAIT, ", "SELECT")]
    [InlineData("CREATE APPLICATION ROLE LibAppRole WITH PASSWORD = 'x', ", "SELECT")]
    [InlineData("SELECT STRING_AGG(Title, ', ') WITHIN ", "SELECT")]
    [InlineData("DELETE FROM dbo.Loan WHERE CURRENT ", "AND")]
    [InlineData("SELECT JSON_VALUE(@j, '$.CopyNo' ", "FROM")]
    [InlineData("CREATE TABLE t (Due datetimeoffset ", "AT")]
    [InlineData("ALTER DATABASE AUDIT SPECIFICATION LibAuditSpec ADD (", "DROP")]
    [InlineData("ALTER DATABASE Lib SET AUTO_CREATE_STATISTICS ON (", "READ_ONLY")]
    [InlineData("TRUNCATE TABLE dbo.Loan WITH (", "SELECT")]
    [InlineData("BACKUP DATABASE LibArchive FILEGROUP = 'a', ", "SELECT")]
    [InlineData("SELECT c.CopyNo FROM dbo.Copy c INNER ", "WHERE")]
    [InlineData("SELECT c.CopyNo FROM dbo.Copy c INNER HASH ", "INTO")]
    [InlineData("WITH ", "SELECT")]
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
    [InlineData("SELECT TRY_PARSE('x' AS date USING ", "USING")]
    [InlineData("DECLARE @x dbo.xml(", "CONTENT")]
    [InlineData("SELECT TOP (10) ", "INTO")]
    [InlineData("EXEC dbo.usp_Copies WITH RECOMPILE, ", "DEFAULT_LANGUAGE")]
    [InlineData("SELECT a, ", "CHECK_POLICY")]
    [InlineData("CREATE LOGIN LibLogin WITH PASSWORD = 'x', ", "DEFAULT_SCHEMA")]
    [InlineData("ALTER LOGIN LibLogin WITH ", "DEFAULT_SCHEMA")]
    [InlineData("CREATE USER LibUser WITH PASSWORD = 'x', ", "CHECK_POLICY")]
    [InlineData("CREATE USER LibUser WITH ", "NAME")]
    [InlineData("CREATE APPLICATION ROLE LibAppRole WITH ", "NAME")]
    [InlineData("CREATE APPLICATION ROLE LibAppRole WITH PASSWORD = 'x', ", "CHECK_POLICY")]
    [InlineData("RAISERROR ('x', 16, 1) WITH NOWAIT, ", "RECOMPILE")]
    [InlineData("EXEC dbo.usp_Copies WITH RECOMPILE, ", "NOWAIT")]
    [InlineData("SELECT a FROM t FOR JSON PATH, ", "NOWAIT")]
    [InlineData("EXECUTE AS USER = 'LibUser' WITH ", "RECOMPILE")]
    [InlineData("GRANT EXECUTE ON dbo.usp_Renew TO LibRole WITH ", "RECOMPILE")]
    [InlineData("RESTORE DATABASE LibArchive FROM DISK = 'x' WITH ", "COMPRESSION")]
    [InlineData("SELECT Title ", "WITHIN")]
    [InlineData("SELECT * FROM dbo.Copy WHERE CopyNo = 1 ", "WITHIN")]
    [InlineData("DBCC CHECKIDENT (", "REPAIR_REBUILD")]
    [InlineData("DBCC CHECKDB (", "RESEED")]
    [InlineData("SELECT CONVERT(int, ", "RESEED")]
    [InlineData("WAITFOR (RECEIVE message_body, ", "GET")]
    [InlineData("SELECT STRING_AGG(Title, ',') WITHIN GROUP (ORDER BY Title, ", "GRAPH")]
    [InlineData("UPDATE dbo.Copy SET Shelf = 1, Floor = 2 ", "ACTION")]
    [InlineData("SELECT BranchId FROM dbo.Copy GROUP BY BranchId ORDER BY BranchId, ", "ROLLUP")]
    [InlineData("SELECT BranchId FROM dbo.Copy GROUP BY BranchId WINDOW w AS (ORDER BY BranchId), ", "ROLLUP")]
    [InlineData("SELECT BranchId FROM dbo.Copy GROUP BY BranchId FOR XML RAW, ", "ROLLUP")]
    [InlineData("SELECT BranchId, CopyNo, ", "ROLLUP")]
    [InlineData("WITH Recent AS (SELECT 1 AS a), ", "XMLNAMESPACES")]
    [InlineData("GRANT SELECT ON dbo.Copy TO LibUser, ", "WITH GRANT OPTION")]
    [InlineData("DENY SELECT ON dbo.Copy TO LibUser, ", "CASCADE")]
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
    [InlineData("SELECT a FROM t GROUP BY a, ", false)]
    [InlineData("SELECT a FROM t INNER ", true)]
    [InlineData("SELECT a FROM t LEFT OUTER LOOP ", true)]
    [InlineData("SELECT a FROM t LEFT OUTER JOIN ", false)]
    [InlineData("SELECT * FROM OPENROWSET (BULK 'x', FORMATFILE = 'f', ORDER (CopyNo ", true)]
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
    [InlineData("DBCC ", true)]
    [InlineData("DBCC CHECKDB WITH ", true)]
    [InlineData("DBCC CHECKDB WITH NO_INFOMSGS, ", true)]
    [InlineData("RAISERROR ('x', 16, 1) WITH NOWAIT, ", true)]
    [InlineData("SELECT a FROM t FOR JSON PATH, ", true)]
    [InlineData("EXEC dbo.usp_Copies @Branch = 1 WITH ", true)]
    [InlineData("SELECT STRING_AGG(Title, ', ') WITHIN ", true)]
    [InlineData("DELETE FROM dbo.Loan WHERE CURRENT ", true)]
    [InlineData("SELECT CopyNo FROM dbo.Copy WHERE CONTAINS(", false)]
    [InlineData("CREATE FULLTEXT STOPLIST CopyStoplist FROM ", false)]
    [InlineData("DBCC CHECKIDENT (", false)]
    [InlineData("DBCC CHECKDB (N'LibArchive', ", false)]
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
    /// 語句寫到片語為止已經完整又換了行：下一句與這一句的續寫都可能，片語只加字。
    /// </summary>
    /// <remarks>
    /// 以前只在前一格判不出位置時這樣做，判得出時略過片語，<c>CREATE SEQUENCE</c> 換行之後的選項、
    /// <c>CREATE DATABASE … ON (…)</c> 換行之後的 <c>LOG ON</c> 都列不出來。
    /// </remarks>
    [Theory]
    [InlineData("CREATE SEQUENCE dbo.LoanNo\n", "START", "SELECT")]
    [InlineData("CREATE SEQUENCE dbo.LoanNo AS int\nSTART WITH 1\n", "INCREMENT", "SELECT")]
    [InlineData("CREATE DATABASE LibArchive\nON (NAME = LibData, FILENAME = 'x')\n", "LOG", "SELECT")]
    [InlineData("CREATE INDEX IX_Loan ON dbo.Loan (CopyNo)\n", "INCLUDE", "SELECT")]
    [InlineData("ALTER TABLE dbo.Loan ADD Note varchar(100)\n", "SPARSE", "SELECT")]
    [InlineData("ALTER TABLE dbo.Loan ALTER COLUMN Note varchar(100) NOT NULL\n", "WITH", "SELECT")]
    public void 語句寫完又換行時片語只加字(string textBeforeCaret, string phraseWord, string nextStatementWord)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var offered = Offered(textBeforeCaret);

        Assert.False(context.ClausePhrase!.IsCertain);
        Assert.Contains(phraseWord, offered);
        Assert.Contains(nextStatementWord, offered);
    }

    /// <summary>
    /// 寫完一句的片語是 IF 只有一句的主體時另接 ELSE；清單照樣封閉，同一行的下一句照樣不列。
    /// </summary>
    /// <remarks>
    /// 位置分析判不出 <c>COMMIT </c> 之後，游標處沒有 IfBodyEnd；比對確定時字又只來自片語，ELSE 兩邊都落空。
    /// </remarks>
    [Theory]
    [InlineData("IF 1 = 1 COMMIT ", true)]
    [InlineData("IF 1 = 1 ALTER INDEX Ix ON dbo.Loan REBUILD ", true)]
    [InlineData("IF 1 = 1 BULK INSERT dbo.Loan FROM 'x' ", true)]
    [InlineData("IF 1 = 1 SELECT CopyNo FROM dbo.Copy ORDER BY CopyNo OFFSET 0 ROWS ", true)]
    [InlineData("COMMIT ", false)]
    [InlineData("WHILE 1 = 1 COMMIT ", false)]
    [InlineData("IF 1 = 1 COMMIT ELSE COMMIT ", false)]
    [InlineData("IF 1 = 1 SELECT 1; COMMIT ", false)]
    public void 寫完一句的片語是IF的單句主體時另接ELSE(string textBeforeCaret, bool expected)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var offered = Offered(textBeforeCaret);

        Assert.True(context.ClausePhrase!.IsCertain);
        Assert.Equal(expected, offered.Contains("ELSE"));
        Assert.DoesNotContain("SELECT", offered);
    }

    /// <summary>
    /// 前一格同時是幾個位置時，同一條尾巴在每個位置接的字都算：查詢寫完換了行，FETCH 可以是 FETCH APPROX，
    /// 也可以是下一句資料指標的 FETCH。
    /// </summary>
    /// <remarks>只取第一個對上的話，換行寫的 <c>FETCH APPROXIMATE</c> 列不出 APPROXIMATE。</remarks>
    [Fact]
    public void 同一條尾巴在幾個位置成立時字取聯集()
    {
        var match = SqlKeywordPositionAnalyzer.Analyze("SELECT a FROM t ORDER BY a\nFETCH ").Phrase;

        Assert.NotNull(match);
        Assert.True(match!.IsCertain);
        Assert.Contains("APPROXIMATE", match.Phrase.Words);
        Assert.Contains("PRIOR", match.Phrase.Words);
        Assert.Same(match.Phrase, SqlKeywordPositionAnalyzer.Analyze("SELECT b FROM u ORDER BY b\nFETCH ").Phrase!.Phrase);
    }

    /// <summary>
    /// 一格同時是幾個位置時，接得上的附加片語全部算數：函式呼叫之後是選取清單尾端，也是函式呼叫之後。
    /// </summary>
    /// <remarks>只取第一個對上的話，<c>AT</c> 與 <c>WITHIN</c> 只剩一個。</remarks>
    [Fact]
    public void 同時對上的附加片語取聯集()
    {
        var match = SqlKeywordPositionAnalyzer.Analyze("SELECT STRING_AGG(Title, ', ') ").Phrase;

        Assert.NotNull(match);
        Assert.False(match!.IsCertain);
        Assert.True(match.Phrase.IsAdditive);
        Assert.Contains("AT", match.Phrase.Words);
        Assert.Contains("WITHIN", match.Phrase.Words);
        Assert.Same(match.Phrase, SqlKeywordPositionAnalyzer.Analyze("SELECT MAX(Fee) ").Phrase!.Phrase);
    }

    /// <summary>
    /// 只認位置的片語對上了，游標處還疊著運算元之後：那個位置的附加片語照樣加字，確定與封閉照位置片語。
    /// </summary>
    /// <remarks>只取位置片語的話，資料行預設值之後列不出 <c>AT</c>；當成可能的話，整份目錄跟著進場。</remarks>
    [Fact]
    public void 位置片語併上疊著的位置的附加片語()
    {
        var match = SqlKeywordPositionAnalyzer.Analyze("CREATE TABLE t (Due datetimeoffset DEFAULT @Due ").Phrase;

        Assert.NotNull(match);
        Assert.True(match!.IsCertain);
        Assert.True(match.IsClosed);
        Assert.Contains("AT", match.Phrase.Words);
        Assert.Contains("CONSTRAINT", match.Phrase.Words);
        Assert.Same(match.Phrase, SqlKeywordPositionAnalyzer.Analyze("CREATE TABLE t (Neg AS -Fee ").Phrase!.Phrase);
    }

    /// <summary>
    /// <c>...</c> 代表動詞之後的其餘標頭：前後要是字面字、只能一個。
    /// </summary>
    [Theory]
    [InlineData("... WITH ,*")]
    [InlineData("EXEC ...")]
    [InlineData("EXEC ... ... WITH")]
    [InlineData("EXEC ... {name} WITH")]
    [InlineData("EXEC () ... WITH")]
    public void 其餘標頭要夾在兩個字面字之間(string pattern)
    {
        Assert.Throws<FormatException>(() => new SqlClausePhrase(pattern, SqlKeywordPosition.StatementStart, string.Empty, false, false, Array.Empty<string>()));
    }

    /// <summary>
    /// <c>...</c> 從這一句的動詞算起，中間至少一個詞元，而且第一個不是關鍵字。
    /// </summary>
    /// <remarks>
    /// <c>EXECUTE AS USER = 'u' WITH NO REVERT</c> 是另一種敘述，<c>GRANT EXECUTE ON … WITH GRANT OPTION</c>
    /// 的 EXECUTE 是權限；兩者都不是程序呼叫的選項清單。
    /// </remarks>
    [Theory]
    [InlineData("EXEC dbo.usp_Renew WITH ", true)]
    [InlineData("EXEC @rc = dbo.usp_Renew @CopyNo = 1 WITH ", true)]
    [InlineData("SELECT 1;\nEXEC [dbo].[usp_Renew] N'x', 1 WITH ", true)]
    [InlineData("EXEC WITH ", false)]
    [InlineData("EXECUTE AS USER = 'LibUser' WITH ", false)]
    [InlineData("GRANT EXECUTE ON dbo.usp_Renew TO LibRole WITH ", false)]
    [InlineData("SELECT 1 FROM t WITH ", false)]
    public void 其餘標頭從動詞算起(string textBeforeCaret, bool matches)
    {
        var match = SqlKeywordPositionAnalyzer.Analyze(textBeforeCaret).Phrase;

        Assert.Equal(matches, match is { IsCertain: true } && match.Phrase.Pattern == "EXEC ... WITH");
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
    /// 中段的 <c>,*</c> 走過清單項時走不出這一句，標頭前一格也要對得上：UPDATE 的 SET、
    /// 同一段裡前一句的 ADD FILE 都不算。
    /// </summary>
    [Theory]
    [InlineData("UPDATE dbo.Loan SET Fee = 1, ")]
    [InlineData("ALTER DATABASE Lib ADD FILE (NAME = Lib2) SELECT * FROM dbo.Loan WHERE Fee IN (")]
    public void 清單項走不出這一句(string textBeforeToken)
    {
        var phrase = SqlKeywordPositionAnalyzer.Analyze(textBeforeToken).Phrase;

        Assert.True(phrase is null || !phrase.Phrase.Pattern.Contains(",*"), phrase?.Phrase.Pattern);
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
    /// 前一格判不出位置時，<c>WITH (</c> 可能是資料表提示，也可能是片語的選項清單：兩份都列。
    /// </summary>
    /// <remarks>
    /// <c>USE d</c> 之後同一行接著寫的那一句，位置分析說不出它從語句開頭寫起，片語只比對到可能；
    /// 提示的封閉清單丟掉片語的話，<c>ONLINE</c> 就列不出來。
    /// </remarks>
    [Theory]
    [InlineData("USE Lib ALTER TABLE dbo.Loan REBUILD WITH (", "ONLINE")]
    [InlineData("BEGIN TRAN ALTER INDEX ALL ON dbo.Loan REBUILD WITH (", "ONLINE")]
    [InlineData("EXEC sp_who ALTER SERVER AUDIT LibAudit WITH (", "STATE")]
    public void 判不出前一格時提示清單也接上片語的字(string textBeforeCaret, string word)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.Equal(CompletionTarget.TableHint, context.Target);
        Assert.Contains(word, Offered(textBeforeCaret));
    }

    /// <summary>
    /// 前一句判不出位置、換了行又寫一句的開頭，那一句的片語比對確定。
    /// </summary>
    /// <remarks>
    /// 清單片語寫完一項（<c>WITH IDENTITY = 'x'</c>）之後判不出位置；下一行的 <c>CREATE CREDENTIAL</c>
    /// 只比對到可能的話，它的 <c>WITH</c> 清單認不出來，逗號之後列不出 <c>SECRET</c>。
    /// </remarks>
    [Theory]
    [InlineData("USE Lib\nALTER TABLE dbo.Loan REBUILD WITH (", "ONLINE")]
    [InlineData("EXEC sp_who\nALTER SERVER AUDIT LibAudit WITH (", "STATE")]
    [InlineData("CREATE CREDENTIAL LibCredential WITH IDENTITY = 'x'\nCREATE CREDENTIAL LibCredential WITH IDENTITY = 'x', ", "SECRET")]
    public void 判不出前一格又換行的語句開頭比對確定(string textBeforeCaret, string word)
    {
        var match = SqlKeywordPositionAnalyzer.Analyze(textBeforeCaret).Phrase;

        Assert.True(match is { IsCertain: true });
        Assert.Contains(word, Offered(textBeforeCaret));
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

        // 清單片語的標頭另有一條片語，逐字由那一條檢查；清單片語的探測文字多了寫完的第一項。
        foreach (var phrase in SqlClausePhraseCatalog.All.Where(phrase => !phrase.IsList))
        {
            var items = phrase.Pattern.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var tokens = SqlTokenizer.Tokenize(phrase.Probe);
            var analyzer = SqlKeywordPositionAnalyzer.ForScript(tokens, phrase.Probe);
            var isLead = phrase.After == SqlKeywordPosition.Any;

            for (var index = isLead ? 1 : 0; index < items.Length; index++)
            {
                if (!IsLiteral(items[index]) || (index > 0 && !IsLiteral(items[index - 1])) ||
                    (isLead && (index < 1 || (index < 2 && SqlKeywordCatalog.IsKeyword(items[0])))))
                {
                    continue;
                }

                // 前面那段直接取自探測文字：從尾端比對到這個字為止，名稱與括號的代入就與產生器相同。
                // ... 要從動詞比對，拆不開：它前面的字由整條片語定位，那一段都是字面字，一個字一個詞元。
                var gap = Array.IndexOf(items, "...");
                int start;

                if (index < gap)
                {
                    Assert.True(items.Take(gap).All(IsLiteral), phrase.Pattern);
                    start = phrase.MatchTail(tokens, tokens.Count, analyzer) + index;
                }
                else
                {
                    var rest = new SqlClausePhrase(
                        string.Join(" ", items.Skip(index)), SqlKeywordPosition.Any, string.Empty,
                        isClosed: false, endsStatement: false, Array.Empty<string>());
                    start = rest.MatchTail(tokens, tokens.Count, analyzer);
                }
                data.Add(phrase.Pattern, phrase.Probe.Substring(0, tokens[start].Start), items[index]);
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
    /// 範圍與這裡相同——前面那段以字面字結尾，Lead 片語至少兩項（單獨一個不是關鍵字的也算），理由見產生器；帶 After 的片語
    /// 第一個字前面那段就是位置，由那個位置的片語或關鍵字目錄給。
    /// </remarks>
    [Theory]
    [MemberData(nameof(PhraseSteps))]
    public void 片語裡的每一個字在前面那段列得出來(string pattern, string textBeforeToken, string word)
    {
        Assert.True(Offered(textBeforeToken).Contains(word), $"{pattern}：{textBeforeToken}| 沒有 {word}");
    }

    /// <summary>
    /// 片段開頭的字從片語一路接得下去時，片段也屬於那一格：<c>CURSOR FOR</c> 之後的清單封閉在
    /// <c>SELECT</c>、<c>WITH</c>，<c>ssf</c> 仍然要在。只接得上第一個字的不算——觸發程序
    /// <c>FOR</c> 之後接得上 <c>INSERT</c>，卻不是 <c>INSERT INTO</c>。
    /// </summary>
    [Theory]
    [InlineData("DECLARE c CURSOR FOR ", "ssf", true)]
    [InlineData("DECLARE c CURSOR LOCAL FAST_FORWARD FOR\n    ", "st100", true)]
    [InlineData("DECLARE c CURSOR FOR ", "sd", true)]
    [InlineData("DECLARE c CURSOR FOR ", "ii", false)]
    [InlineData("CREATE TRIGGER tr ON dbo.Loan FOR ", "ii", false)]
    [InlineData("ALTER TABLE dbo.Loan ", "dt", false)]
    [InlineData("UPDATE dbo.Loan ", "sno", false)]
    public void 片語的字開頭的片段也屬於那一格(string textBeforeCaret, string shortcut, bool expected)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret + shortcut);
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(context.ClausePhrase?.Suggestions ?? Enumerable.Empty<SqlSuggestion>());

        var offered = SuggestionListProbe.Match(candidates, context)
            .Any(suggestion => suggestion.Kind == SuggestionKind.Snippet && suggestion.DisplayText == shortcut);

        Assert.Equal(expected, offered);
    }

    private static bool IsLiteral(string item)
    {
        return char.IsLetter(item[0]) || item[0] == '_';
    }

    /// <summary>
    /// 走產品的過濾路徑：候選清單加上片語的字，再做上下文過濾。唯一接續併成的一項（<c>INSTEAD OF</c>）
    /// 也算它的第一個字：打那個字就選得到它。ScriptDom 當識別字的資料列集函式（<c>VECTOR_SEARCH</c>）由函式目錄列。
    /// </summary>
    private static string[] Offered(string textBeforeToken)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeToken);
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(context.ClausePhrase?.Suggestions ?? Enumerable.Empty<SqlSuggestion>());

        return SuggestionContextFilter.Filter(candidates, context)
            .Where(suggestion => suggestion.Kind is SuggestionKind.Keyword or SuggestionKind.BuiltInFunction)
            .SelectMany(suggestion => new[] { suggestion.DisplayText, suggestion.DisplayText.Split(' ')[0] })
            .Distinct()
            .ToArray();
    }
}
