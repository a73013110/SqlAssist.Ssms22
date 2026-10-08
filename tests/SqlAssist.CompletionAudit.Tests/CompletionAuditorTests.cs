using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.CompletionAudit.Tests;

/// <summary>
/// 判定本身：哪些詞元算答案、漏的樣子與簽章、範例怎麼遮。
/// </summary>
/// <remarks>
/// 內建清單給空的，字一定列不出來：這裡驗的是判定，不是產品目前補到哪裡——產品補上一個缺口
/// 不該讓這些測試失敗。
/// </remarks>
public sealed class CompletionAuditorTests
{
    [Fact]
    public async Task 列不出來的字記成不在候選_簽章只看前兩個詞元的形狀()
    {
        var result = await AuditAsync("SELECT 1", AuditCatalog.None);

        var miss = Assert.Single(result.Misses);
        Assert.Equal(AuditTokenClass.Word, miss.TokenClass);
        Assert.Equal(AuditMissKind.Absent, miss.Kind);
        Assert.Equal("^ ^ ⎵SELECT [Absent]", miss.Signature.Text);
        Assert.Equal(1, result.Tally.Excluded[AuditExclusion.Literal]);
    }

    [Fact]
    public async Task 沒有資料庫時名稱查不到存在_排除不算漏()
    {
        var result = await AuditAsync("SELECT Lib_Reader.ReaderId", AuditCatalog.None);

        Assert.DoesNotContain(result.Misses, miss => miss.TokenClass != AuditTokenClass.Word);
        Assert.Equal(2, result.Tally.Excluded[AuditExclusion.Unresolved]);
    }

    [Fact]
    public async Task 名稱索引認得的欄位要列得出來()
    {
        var catalog = new FakeCatalog(("ReaderId", AuditTokenClass.Column), ("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync("SELECT * FROM Lib_Reader r WHERE r.ReaderId = 1", catalog);

        Assert.Contains(result.Misses, miss => miss.TokenClass == AuditTokenClass.Column && miss.Word == "ReaderId");
        Assert.Contains(result.Misses, miss => miss.TokenClass == AuditTokenClass.Object && miss.Word == "Lib_Reader");
    }

    [Fact]
    public async Task 別名指的資料表查不到_同名欄位不算漏()
    {
        var catalog = new FakeCatalog(("ReaderId", AuditTokenClass.Column));

        var result = await AuditAsync("SELECT * FROM Lib_Tag r WHERE r.ReaderId = 1", catalog);

        Assert.DoesNotContain(result.Misses, miss => miss.Word == "ReaderId");
        Assert.True(result.Tally.Excluded[AuditExclusion.Unresolved] >= 2);
    }

    [Fact]
    public async Task 限定字查不到存在_點號之後不稽核()
    {
        var catalog = new FakeCatalog(("ReaderId", AuditTokenClass.Column));

        var result = await AuditAsync("SELECT * FROM Lib_Reader WHERE Lib_Tag.ReaderId = 1", catalog);

        Assert.DoesNotContain(result.Misses, miss => miss.Word == "ReaderId");
    }

    [Theory]
    [InlineData("INSERT INTO Lib_Tag (\nReaderId,\nPUBL_CODE) VALUES (1, 2)")]
    [InlineData("INSERT INTO LibArchive.dbo.Lib_Reader (ReaderId, PUBL_CODE) VALUES (1, 2)")]
    [InlineData("CREATE INDEX IX_Tag ON Lib_Tag (ReaderId, PUBL_CODE) INCLUDE (ReaderId)")]
    [InlineData("MERGE Lib_Tag t USING Lib_Reader s ON 1 = 1 WHEN NOT MATCHED THEN INSERT (ReaderId, PUBL_CODE) VALUES (1, 2);")]
    [InlineData("ALTER TABLE Lib_Tag ADD CONSTRAINT PK_Tag PRIMARY KEY (ReaderId, PUBL_CODE)")]
    [InlineData("CREATE STATISTICS ST_Tag ON Lib_Tag (ReaderId, PUBL_CODE)")]
    public async Task 資料行清單的擁有者查不到_同名欄位不算漏(string sql)
    {
        var catalog = new FakeCatalog(
            ("ReaderId", AuditTokenClass.Column),
            ("PUBL_CODE", AuditTokenClass.Column),
            ("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync(sql, catalog);

        Assert.DoesNotContain(result.Misses, miss => miss.Word is "ReaderId" or "PUBL_CODE");
    }

    [Theory]
    [InlineData("SELECT 1 FROM LibArchive.dbo.Lib_Reader GROUP BY ReaderId, PUBL_CODE")]
    [InlineData("SELECT 1 FROM Lib_Reader r JOIN (SELECT 1 AS n FROM Lib_Tag GROUP BY ReaderId, PUBL_CODE) d ON 1 = 1")]
    public async Task 查詢的來源全都查不到_沒寫限定字的同名欄位不算漏(string sql)
    {
        var catalog = new FakeCatalog(
            ("ReaderId", AuditTokenClass.Column),
            ("PUBL_CODE", AuditTokenClass.Column),
            ("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync(sql, catalog);

        Assert.DoesNotContain(result.Misses, miss => miss.Word is "ReaderId" or "PUBL_CODE");
        Assert.True(result.Tally.Excluded[AuditExclusion.Unresolved] >= 2);
    }

    [Fact]
    public async Task 外層查詢的來源認得_子查詢裡的欄位照常稽核()
    {
        var catalog = new FakeCatalog(("PUBL_CODE", AuditTokenClass.Column), ("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync("SELECT 1 FROM Lib_Reader WHERE EXISTS (SELECT 1 FROM Lib_Tag WHERE PUBL_CODE = 1)", catalog);

        Assert.Contains(result.Misses, miss => miss.TokenClass == AuditTokenClass.Column && miss.Word == "PUBL_CODE");
    }

    /// <summary>
    /// 內層的表寫在連線看不到的資料庫：表一定存在、欄位可能是它的，外層的表說不上是擁有者。
    /// 名稱索引沒有的內層表照上一條不擋。
    /// </summary>
    [Fact]
    public async Task 內層的表在看不到的資料庫_外層的表不算擁有者()
    {
        var catalog = new FakeCatalog(
            ("PUBL_CODE", AuditTokenClass.Column),
            ("Lib_Reader", AuditTokenClass.Object),
            ("Lib_Tag", AuditTokenClass.Object));

        var result = await AuditAsync(
            "UPDATE r SET Name = 1 FROM Lib_Reader r WHERE EXISTS (SELECT 1 FROM LibArchive.dbo.Lib_Tag WHERE PUBL_CODE = 1)",
            catalog);

        Assert.DoesNotContain(result.Misses, miss => miss.Word == "PUBL_CODE");
    }

    /// <summary>
    /// DML 的目標是 FROM 取的別名：指的是 FROM 那張表，別名本身不算認得的擁有者。
    /// 那張表寫在看不到的資料庫時，本地同名資料表的欄位不算漏。
    /// </summary>
    [Theory]
    [InlineData("UPDATE r SET Name = @n FROM LibArchive.dbo.Lib_Reader r WHERE PUBL_CODE = 1")]
    [InlineData("DELETE r FROM LibArchive.dbo.Lib_Reader r WHERE PUBL_CODE = 1")]
    public async Task 目標是別名而來源在看不到的資料庫_同名欄位不算漏(string sql)
    {
        var catalog = new FakeCatalog(("PUBL_CODE", AuditTokenClass.Column), ("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync(sql, catalog);

        Assert.DoesNotContain(result.Misses, miss => miss.Word == "PUBL_CODE");
        Assert.True(result.Tally.Excluded[AuditExclusion.Unresolved] >= 1);
    }

    [Fact]
    public async Task 目標是別名而來源認得_欄位照常稽核()
    {
        var catalog = new FakeCatalog(("PUBL_CODE", AuditTokenClass.Column), ("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync("UPDATE r SET Name = @n FROM Lib_Reader r WHERE PUBL_CODE = 1", catalog);

        Assert.Contains(result.Misses, miss => miss.TokenClass == AuditTokenClass.Column && miss.Word == "PUBL_CODE");
    }

    [Theory]
    [InlineData("CREATE TABLE Lib_Tag (ReaderId int,\n    PUBL_CODE int,\n    PRIMARY KEY (ReaderId,\n    PUBL_CODE))")]
    [InlineData("CREATE TABLE Lib_Tag (ReaderId int,\n    PUBL_CODE int NOT NULL -- 出版者\n    PRIMARY KEY (ReaderId,\n    PUBL_CODE))")]
    [InlineData("DECLARE @Tag TABLE (ReaderId int, PUBL_CODE int, UNIQUE (ReaderId, PUBL_CODE))")]
    public async Task 資料表定義的條件約束清單引用同一份定義的資料行_產品列得出來(string sql)
    {
        var catalog = new FakeCatalog(("ReaderId", AuditTokenClass.Column), ("PUBL_CODE", AuditTokenClass.Column));

        var result = await AuditAsync(sql, catalog);

        Assert.DoesNotContain(result.Misses, miss => miss.Word is "ReaderId" or "PUBL_CODE");
        Assert.False(result.Tally.Excluded.ContainsKey(AuditExclusion.Unresolved));
    }

    [Theory]
    [InlineData("INSERT INTO Lib_Reader (ReaderId, PUBL_CODE) VALUES (1, 2)")]
    [InlineData("CREATE INDEX IX_Reader ON Lib_Reader (ReaderId, PUBL_CODE)")]
    [InlineData("MERGE Lib_Reader t USING Lib_Tag s ON 1 = 1 WHEN NOT MATCHED THEN INSERT (ReaderId, PUBL_CODE) VALUES (1, 2);")]
    [InlineData("CREATE TABLE #Reader (ReaderId int)\nINSERT INTO #Reader (ReaderId, PUBL_CODE) VALUES (1, 2)")]
    public async Task 資料行清單的擁有者認得_欄位照常稽核(string sql)
    {
        var catalog = new FakeCatalog(
            ("ReaderId", AuditTokenClass.Column),
            ("PUBL_CODE", AuditTokenClass.Column),
            ("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync(sql, catalog);

        Assert.Contains(result.Misses, miss => miss.TokenClass == AuditTokenClass.Column && miss.Word == "PUBL_CODE");
    }

    [Theory]
    [InlineData("SELECT ReaderId, PUBL_CODE FROM Lib_Reader", 2)]
    [InlineData("SELECT r.ReaderId FROM Lib_Reader r", 2)]
    [InlineData("WITH c (ReaderId) AS (SELECT 1) SELECT ReaderId FROM c", 1)]
    public async Task 資料來源寫在游標之後_歸截斷盲點(string sql, int truncated)
    {
        var catalog = new FakeCatalog(
            ("ReaderId", AuditTokenClass.Column),
            ("PUBL_CODE", AuditTokenClass.Column),
            ("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync(sql, catalog);

        Assert.DoesNotContain(result.Misses, miss => miss.TokenClass is AuditTokenClass.Column or AuditTokenClass.ScriptName);
        Assert.Equal(truncated, result.Tally.Excluded[AuditExclusion.Truncated]);
    }

    /// <summary><c>dbo.Geo::Cv</c> 的 Cv 是型別的靜態成員，不是同一句 <c>T(Cv)</c> 取的資料行別名。</summary>
    [Fact]
    public async Task 雙冒號之後的成員不是指令碼取的名稱()
    {
        var result = await AuditAsync(
            "DECLARE @x xml;\nSELECT T.Cv.value('.', 'int') FROM @x.nodes('/a') AS T(Cv) WHERE dbo.Geo::Cv(1) IS NULL",
            AuditCatalog.None);

        Assert.DoesNotContain(result.Misses, miss => miss.Word == "Cv");
        Assert.Equal(0, result.Tally.UnparsedStatements);
    }

    [Fact]
    public async Task UPDATE_的目標是FROM才取的別名_SET的欄位歸截斷盲點()
    {
        var catalog = new FakeCatalog(
            ("ReaderId", AuditTokenClass.Column),
            ("PUBL_CODE", AuditTokenClass.Column),
            ("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync("UPDATE r SET ReaderId = @Id, PUBL_CODE = @Code FROM Lib_Reader r", catalog);

        Assert.DoesNotContain(result.Misses, miss => miss.TokenClass == AuditTokenClass.Column);
        Assert.Equal(3, result.Tally.Excluded[AuditExclusion.Truncated]);
    }

    [Theory]
    [InlineData("UPDATE r SET PUBL_CODE = @Code OUTPUT inserted.ReaderId FROM Lib_Reader r")]
    [InlineData("UPDATE r SET PUBL_CODE = @Code\n-- 換行之前的註解\nOUTPUT deleted.PUBL_CODE, inserted.ReaderId INTO @t FROM Lib_Reader r")]
    [InlineData("DELETE r OUTPUT deleted.ReaderId FROM Lib_Reader r")]
    public async Task OUTPUT_的目標是FROM才取的別名_inserted的欄位歸截斷盲點(string sql)
    {
        var catalog = new FakeCatalog(
            ("ReaderId", AuditTokenClass.Column),
            ("PUBL_CODE", AuditTokenClass.Column),
            ("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync("DECLARE @Code int, @t TABLE (a int, b int);\n" + sql, catalog);

        Assert.DoesNotContain(result.Misses, miss => miss.TokenClass == AuditTokenClass.Column);
    }

    [Fact]
    public async Task OUTPUT_的目標是資料表名稱_inserted的欄位照常稽核()
    {
        var catalog = new FakeCatalog(("ReaderId", AuditTokenClass.Column), ("Lib_Reader", AuditTokenClass.Object));
        const string sql = "UPDATE Lib_Reader SET ReaderId = 1 OUTPUT inserted.ReaderId FROM Lib_Reader JOIN Lib_Tag t ON 1 = 1";

        var result = await AuditAsync(sql, catalog);

        Assert.Contains(
            result.Misses,
            miss => miss.TokenClass == AuditTokenClass.Column && miss.Offset == sql.IndexOf("inserted.", StringComparison.Ordinal) + 9);
    }

    [Fact]
    public async Task UPDATE_的目標是資料表名稱_SET的欄位照常稽核()
    {
        var catalog = new FakeCatalog(("ReaderId", AuditTokenClass.Column), ("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync("UPDATE Lib_Reader SET ReaderId = @Id FROM Lib_Reader JOIN Lib_Tag t ON 1 = 1", catalog);

        Assert.Contains(result.Misses, miss => miss.TokenClass == AuditTokenClass.Column && miss.Word == "ReaderId");
    }

    [Fact]
    public async Task FROM_之後的欄位照常稽核()
    {
        var catalog = new FakeCatalog(("ReaderId", AuditTokenClass.Column), ("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync("SELECT 1 FROM Lib_Reader WHERE ReaderId = 1", catalog);

        Assert.Contains(result.Misses, miss => miss.TokenClass == AuditTokenClass.Column && miss.Word == "ReaderId");
        Assert.False(result.Tally.Excluded.ContainsKey(AuditExclusion.Truncated));
    }

    [Theory]
    [InlineData("CREATE LOGIN <login_name> WITH PASSWORD = 'x', CREDENTIAL = c\nSELECT 1")]
    [InlineData("CREATE PROCEDURE <Procedure_Name, sysname, p> AS\nSELECT 1")]
    public async Task 佔位符起到那一句結束不稽核(string sql)
    {
        var result = await AuditAsync(sql, AuditCatalog.None);

        Assert.Equal("SELECT", result.Misses.Last().Word);
        Assert.DoesNotContain(result.Misses, miss => miss.Word is "WITH" or "PASSWORD" or "CREDENTIAL" or "AS");
        Assert.True(result.Tally.Excluded[AuditExclusion.Placeholder] > 0);
    }

    [Fact]
    public async Task 比較運算不是佔位符()
    {
        var result = await AuditAsync("SELECT 1 WHERE @a <@b AND @c> 1", AuditCatalog.None);

        Assert.False(result.Tally.Excluded.ContainsKey(AuditExclusion.Placeholder));
    }

    [Fact]
    public async Task 剖析不過的語法片段不記漏_之後剖析得過的句子照常稽核()
    {
        var result = await AuditAsync("Lib_Reader (ReaderId INT, PUBL_CODE VARCHAR(10))\nSELECT 1", AuditCatalog.None);

        Assert.DoesNotContain(result.Misses, miss => miss.Word is "INT" or "VARCHAR");
        Assert.Equal("SELECT", Assert.Single(result.Misses).Word);
        Assert.Equal(1, result.Tally.UnparsedStatements);
        Assert.True(result.Tally.Excluded[AuditExclusion.Unparsed] > 0);
    }

    [Fact]
    public async Task 剖析得過的照常稽核_不記剖析失敗()
    {
        var result = await AuditAsync("SELECT ReaderId FROM Lib_Reader WHERE ReaderId = 1", AuditCatalog.None);

        Assert.Equal(0, result.Tally.UnparsedStatements);
        Assert.False(result.Tally.Excluded.ContainsKey(AuditExclusion.Unparsed));
        Assert.Equal(new[] { "SELECT", "FROM", "WHERE" }, result.Misses.Select(miss => miss.Word));
    }

    [Fact]
    public async Task 緊貼數值的字是常值的一部分()
    {
        var result = await AuditAsync("ALTER DATABASE d MODIFY FILE (NAME = f, SIZE = 20MB, MAXSIZE = 40 MB)", AuditCatalog.None);

        Assert.DoesNotContain(result.Misses, miss => miss.Word == "MB");
        Assert.Equal(3, result.Tally.Excluded[AuditExclusion.Literal]);
    }

    [Fact]
    public async Task 剖析不過的那一句錯之前_不是保留字的字說不出角色()
    {
        var result = await AuditAsync(
            "INSERT INTO Lib_Tag (Name, PUBL_CODE) VALUES (1, 2), (SELECT Name, PUBL_CODE FROM Lib_Reader)",
            AuditCatalog.None);

        Assert.Contains(result.Misses, miss => miss.Word == "INSERT");
        Assert.DoesNotContain(result.Misses, miss => miss.Word.Equals("NAME", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 守大寫慣例的語料_大小寫混合的是名稱_同名的字不算()
    {
        var result = await new CompletionAuditor(new SqlAssistSettings(), Array.Empty<SqlSuggestion>(), AuditMask.None)
            .AuditAsync(new AuditFragment("test", "t", "SELECT 1 FROM Copy WHERE 1 = 1", wordsInUpperCase: true), AuditCatalog.None, CancellationToken.None);

        Assert.DoesNotContain(result.Misses, miss => miss.Word == "Copy");
        Assert.Equal(1, result.Tally.Excluded[AuditExclusion.Unresolved]);
    }

    [Fact]
    public async Task 新取的名稱與常值不稽核()
    {
        var result = await AuditAsync("DECLARE @n int = 5", AuditCatalog.None);

        Assert.Equal(1, result.Tally.Excluded[AuditExclusion.NewName]);
        Assert.Equal(1, result.Tally.Excluded[AuditExclusion.Literal]);
    }

    [Fact]
    public async Task GOTO_標籤的定義是新取的名稱()
    {
        var result = await AuditAsync("start:\nSELECT 1",AuditCatalog.None);

        Assert.DoesNotContain(result.Misses, miss => miss.Word == "START");
        Assert.Equal(1, result.Tally.Excluded[AuditExclusion.NewName]);
    }

    [Theory]
    [InlineData("CREATE PROCEDURE p @a varchar(20) = false AS SELECT 1", "FALSE")]
    [InlineData("EXECUTE sp_serveroption 'x', 'rpc out', true", "TRUE")]
    public async Task 參數預設值與EXEC引數寫成識別字是常值(string sql, string word)
    {
        var result = await AuditAsync(sql, AuditCatalog.None);

        Assert.DoesNotContain(result.Misses, miss => miss.Word == word);
        Assert.Contains(result.Misses, miss => miss.Word == "SELECT" || miss.Word == "EXECUTE");
    }

    [Theory]
    [InlineData("SET DEADLOCK_PRIORITY LOW", 3)]
    [InlineData("SELECT DATEADD(day, 1, 2)", 3)]
    [InlineData("EXECUTE p @a = DEFAULT", 2)]
    public async Task 值以外的識別字常值照常稽核(string sql, int words)
    {
        var result = await AuditAsync(sql, AuditCatalog.None);

        Assert.Equal(words, result.Tally.Audited[AuditTokenClass.Word]);
        Assert.False(result.Tally.Excluded.ContainsKey(AuditExclusion.Placeholder));
    }

    [Fact]
    public async Task 日期部分那一格不是日期部分_是佔位符()
    {
        var result = await AuditAsync("SELECT DATENAME(datepart, 1)", AuditCatalog.None);

        Assert.DoesNotContain(result.Misses, miss => miss.Word == "DATEPART");
        Assert.Equal(1, result.Tally.Excluded[AuditExclusion.Placeholder]);
    }

    [Fact]
    public async Task 範例遮掉名稱_常值與註解內容()
    {
        var catalog = new FakeCatalog(("Lib_Reader", AuditTokenClass.Object));

        var result = await AuditAsync("-- 讀者 Lib_Reader.\nSELECT 'x' FROM Lib_Reader WHERE", catalog, new BracketMask());

        var miss = result.Misses.Single(item => item.Word == "WHERE");
        Assert.Equal("--…\nSELECT '…' FROM <Object> ⎵WHERE", miss.MaskedExample);
        Assert.DoesNotContain("讀者", miss.MaskedExample);
        Assert.Contains("Lib_Reader", miss.Example);
    }

    [Fact]
    public async Task 同一個位置每一次都是同一群()
    {
        var first = await AuditAsync("SELECT 1", AuditCatalog.None);
        var second = await AuditAsync("\n\nSELECT 2", AuditCatalog.None);

        Assert.Equal(first.Misses.Single().Signature.Id, second.Misses.Single().Signature.Id);
    }

    [Fact]
    public async Task 重驗只問那一個位置()
    {
        var auditor = new CompletionAuditor(new SqlAssistSettings(), Array.Empty<SqlSuggestion>(), AuditMask.None);
        var fragment = new AuditFragment("test", "t", "SELECT 1 FROM t");

        var miss = await auditor.RecheckAsync(fragment, "SELECT 1 ".Length, AuditCatalog.None, new AuditTally(), CancellationToken.None);

        Assert.Equal("FROM", miss?.Word);
    }

    private static Task<AuditResult> AuditAsync(string sql, IAuditCatalog catalog, IAuditMask? mask = null) =>
        new CompletionAuditor(new SqlAssistSettings(), Array.Empty<SqlSuggestion>(), mask ?? AuditMask.None)
            .AuditAsync(new AuditFragment("test", "t", sql), catalog, CancellationToken.None);

    private sealed class BracketMask : IAuditMask
    {
        public string Mask(string name, AuditTokenClass? tokenClass) => $"<{tokenClass}>";
    }

    /// <summary>只認得指定的名稱，清單的資料庫那一份一律是空的。</summary>
    private sealed class FakeCatalog : IAuditCatalog, IAuditNameIndex
    {
        private readonly Dictionary<string, AuditTokenClass> _names;

        public FakeCatalog(params (string Name, AuditTokenClass Class)[] names)
        {
            _names = names.ToDictionary(item => AuditText.Normalize(item.Name), item => item.Class);
        }

        public ISqlCompletionMetadata Metadata => SqlCompletionMetadata.None;

        public Task<IAuditNameIndex> IndexAsync(string batch, IReadOnlyList<SqlToken> tokens, CancellationToken cancellationToken) =>
            Task.FromResult<IAuditNameIndex>(this);

        public AuditTokenClass? Find(string name) =>
            _names.TryGetValue(AuditText.Normalize(name), out var tokenClass) ? tokenClass : null;
    }
}
