using System.Linq;
using Xunit;

namespace SqlAssist.CompletionAudit.Tests;

/// <summary>新取的名稱與它引用得到的範圍：認錯的症狀是把名字相同的欄位當成漏。</summary>
public sealed class AuditDefinitionsTests
{
    [Fact]
    public void 選取清單的別名只在同一句的ORDERBY裡引用得到()
    {
        const string sql = "SELECT a AS x, x FROM t ORDER BY x";
        var definitions = AuditDefinitions.Collect(sql);

        Assert.True(definitions.IsDefinition(At(sql, "x", 1)));
        Assert.False(definitions.IsDefinedBefore("x", At(sql, "x", 2)));
        Assert.True(definitions.IsDefinedBefore("x", At(sql, "x", 3)));
    }

    [Fact]
    public void UNION的下一個分支看不到上一個分支的別名()
    {
        const string sql = "SELECT r.a FROM t r UNION ALL SELECT r.b FROM u AS r";
        var definitions = AuditDefinitions.Collect(sql);

        Assert.False(definitions.IsDefinedBefore("r", At(sql, "r.b", 1)));
    }

    [Fact]
    public void 點號之後只有衍生資料表與CTE的資料行清單算()
    {
        const string sql = "WITH c (a) AS (SELECT 1) SELECT Node = e.x, c.a FROM t e CROSS JOIN c ORDER BY e.Node";
        var definitions = AuditDefinitions.Collect(sql);

        Assert.True(definitions.IsDefinedBefore("a", At(sql, "a FROM", 1), qualified: true));
        Assert.False(definitions.IsDefinedBefore("Node", At(sql, "Node", 2), qualified: true));
    }

    [Fact]
    public void MERGE目標的別名在整句裡引用得到()
    {
        const string sql = "MERGE dbo.Loan t USING dbo.Copy s ON t.CopyNo = s.CopyNo WHEN MATCHED THEN UPDATE SET CopyNo = t.CopyNo;";
        var definitions = AuditDefinitions.Collect(sql);

        Assert.True(definitions.IsDefinition(At(sql, "t ", 1)));
        Assert.True(definitions.IsDefinedBefore("t", At(sql, "t.", 1)));
        Assert.True(definitions.IsDefinedBefore("t", At(sql, "t.", 2)));
    }

    [Fact]
    public void CTE名稱當限定字時要這個查詢的FROM_別名不必()
    {
        const string sql = "WITH c (a) AS (SELECT 1 UNION ALL SELECT c.a + 1 FROM c WHERE c.a < 5) SELECT r.a FROM c r";
        var definitions = AuditDefinitions.Collect(sql);

        Assert.True(definitions.NamesTable("c", At(sql, "c.a", 1)));
        Assert.True(definitions.NeedsLaterFrom(At(sql, "c.a", 1)));
        Assert.False(definitions.NamesTable("r", At(sql, "r.a", 1)));
    }

    [Theory]
    [InlineData("SELECT 1 FROM LibArchive.dbo.Loan l WHERE l.CopyNo = 1", "LibArchive")]
    [InlineData("SELECT 1 FROM dbo.Loan l WHERE l.CopyNo = 1", null)]
    public void 別名的來源連寫出來的資料庫一起帶出(string sql, string? database)
    {
        var source = AuditDefinitions.Collect(sql).SourceOf("l", At(sql, "l.", 1));

        Assert.Equal(("Loan", database), source);
    }

    [Theory]
    [InlineData("INSERT INTO LibArchive.dbo.Loan (CopyNo) VALUES (1)", "Loan", "LibArchive")]
    [InlineData("CREATE INDEX IX_Copy ON dbo.Copy (Branch, CopyNo) INCLUDE (Loan)", "Copy", null)]
    [InlineData("CREATE TABLE Loan (Branch int, CONSTRAINT FK_Copy FOREIGN KEY (Branch) REFERENCES Copy (CopyNo))", "Copy", null)]
    [InlineData("ALTER TABLE Loan ADD PRIMARY KEY (Branch, CopyNo)", "Loan", null)]
    [InlineData("MERGE Loan t USING Copy s ON 1 = 1 WHEN NOT MATCHED THEN INSERT (Branch, CopyNo) VALUES (1, 2);", "Loan", null)]
    [InlineData("SELECT * INTO #Loan FROM LibArchive.dbo.Loan; INSERT INTO #Loan (CopyNo) VALUES (1)", "Loan", "LibArchive")]
    public void 資料行清單的欄位只屬於清單指定的那張表(string sql, string table, string? database)
    {
        var owners = AuditDefinitions.Collect(sql).ColumnOwners(sql.LastIndexOf("CopyNo", System.StringComparison.Ordinal));

        Assert.Equal(new[] { (table, database) }, owners);
    }

    [Theory]
    [InlineData("SELECT 1 FROM LibArchive.dbo.Loan GROUP BY CopyNo", "Loan")]
    [InlineData("SELECT 1 FROM Copy c JOIN (SELECT 1 AS n FROM LibArchive.dbo.Loan GROUP BY CopyNo) d ON 1 = 1", "Loan")]
    [InlineData("SELECT 1 FROM Copy c WHERE EXISTS (SELECT 1 FROM LibArchive.dbo.Loan WHERE CopyNo = 1)", "Loan,Copy")]
    [InlineData("UPDATE Copy SET Branch = 1 FROM Copy JOIN LibArchive.dbo.Loan l ON 1 = 1 WHERE CopyNo = 1", "Copy,Copy,Loan")]
    public void 查詢裡沒寫限定字的欄位屬於範圍內的來源_衍生資料表看不到它那一層(string sql, string tables)
    {
        var owners = AuditDefinitions.Collect(sql).ColumnOwners(sql.LastIndexOf("CopyNo", System.StringComparison.Ordinal));

        Assert.Equal(tables.Split(','), owners!.Select(owner => owner.Name));
    }

    [Theory]
    [InlineData("SELECT 1 FROM Loan JOIN (SELECT 1 AS a) d ON 1 = 1 WHERE CopyNo = 1")]
    [InlineData("DECLARE @t TABLE (CopyNo int); SELECT 1 FROM @t WHERE CopyNo = 1")]
    [InlineData("SELECT CopyNo = 1 FROM Loan ORDER BY CopyNo")]
    [InlineData("DECLARE @t TABLE (CopyNo int); INSERT INTO @t (CopyNo) VALUES (1)")]
    [InlineData("SELECT CopyNo")]
    public void 說不出屬於哪張表時沒有擁有者(string sql)
    {
        Assert.Null(AuditDefinitions.Collect(sql).ColumnOwners(sql.LastIndexOf("CopyNo", System.StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("CREATE TABLE Loan (Branch int, CopyNo int, PRIMARY KEY (Branch, CopyNo))")]
    [InlineData("CREATE TABLE dbo.Loan (Branch int, CopyNo int, INDEX ix (Branch) INCLUDE (CopyNo))")]
    [InlineData("CREATE TABLE Loan (CopyNo int, Branch int REFERENCES Loan (CopyNo))")]
    [InlineData("CREATE TABLE #Loan (CopyNo int, CONSTRAINT uq UNIQUE NONCLUSTERED (CopyNo))")]
    [InlineData("CREATE TABLE Loan (Branch int, CopyNo int NOT NULL PRIMARY KEY (Branch, CopyNo))")]
    [InlineData("DECLARE @Loan TABLE (Branch int, CopyNo int, PRIMARY KEY (Branch, CopyNo))")]
    [InlineData("CREATE TYPE dbo.LoanList AS TABLE (CopyNo int, INDEX ix (CopyNo))")]
    public void 資料表定義自己的條件約束清單引用得到同一份定義的資料行(string sql)
    {
        var definitions = AuditDefinitions.Collect(sql);
        var reference = sql.LastIndexOf("CopyNo", System.StringComparison.Ordinal);

        Assert.True(definitions.IsDefinedBefore("CopyNo", reference));
        Assert.Null(definitions.ColumnOwners(reference));
    }

    [Fact]
    public void 資料行定義只在自己那幾份清單裡引用得到()
    {
        const string sql = "CREATE TABLE Loan (CopyNo int, PRIMARY KEY (CopyNo), CHECK (CopyNo > 0))\nSELECT CopyNo FROM Copy";
        var definitions = AuditDefinitions.Collect(sql);

        Assert.False(definitions.IsDefinedBefore("CopyNo", At(sql, "CopyNo", 3)));
        Assert.False(definitions.IsDefinedBefore("CopyNo", At(sql, "CopyNo", 4)));
    }

    [Fact]
    public void 暫存資料表是SELECT星號INTO一張表時_來源是那張表()
    {
        const string sql = "SELECT * INTO #Loan FROM LibArchive.dbo.Loan; SELECT 1 FROM #Loan l WHERE l.CopyNo = 1";

        Assert.Equal(("Loan", "LibArchive"), AuditDefinitions.Collect(sql).SourceOf("l", At(sql, "l.", 1)));
    }

    [Fact]
    public void 內層之後才取的同名別名遮住外層_歸截斷盲點()
    {
        const string sql = "SELECT 1 FROM #Loan a JOIN (SELECT a.Title FROM Copy a) b ON b.Title = a.CopyNo";
        var definitions = AuditDefinitions.Collect(sql);

        Assert.True(definitions.IsDefinedBefore("a", At(sql, "a.Title", 1)));
        Assert.True(definitions.IsDefinedLater("a", At(sql, "a.Title", 1)));
        Assert.False(definitions.IsDefinedLater("a", At(sql, "a.CopyNo", 1)));
    }

    [Fact]
    public void 之後才取的看最內層_外層之後才取的同名別名不算()
    {
        const string sql = "SELECT (SELECT a.Title FROM Copy a WHERE a.CopyNo = 1) FROM Loan a";
        var definitions = AuditDefinitions.Collect(sql);

        Assert.True(definitions.IsDefinedLater("a", At(sql, "a.Title", 1)));
        Assert.False(definitions.IsDefinedLater("a", At(sql, "a.CopyNo", 1)));
    }

    [Fact]
    public void 資料行定義不讓同名的欄位引用變成指令碼名稱()
    {
        const string sql = "CREATE TABLE Loan (CopyNo int REFERENCES Copy (CopyNo))";
        var definitions = AuditDefinitions.Collect(sql);

        Assert.True(definitions.IsDefinition(At(sql, "CopyNo", 1)));
        Assert.False(definitions.IsDefinition(At(sql, "CopyNo", 2)));
        Assert.False(definitions.IsDefinedBefore("CopyNo", At(sql, "CopyNo", 2)));
    }

    [Fact]
    public void 變數到批次結束都引用得到()
    {
        const string sql = "DECLARE @x int; SELECT 1; SELECT @x";
        var definitions = AuditDefinitions.Collect(sql);

        Assert.True(definitions.IsDefinedBefore("@x", At(sql, "@x", 2)));
    }

    [Fact]
    public void CREATE的目標只有最後一段是新取的()
    {
        const string sql = "CREATE VIEW dbo.v_OpenLoan AS SELECT 1 AS a";
        var definitions = AuditDefinitions.Collect(sql);

        Assert.True(definitions.IsDefinition(At(sql, "v_OpenLoan", 1)));
        Assert.False(definitions.IsDefinition(At(sql, "dbo", 1)));
    }

    [Theory]
    [InlineData("SELECT name, type FROM sys.objects", "type", true)]
    [InlineData("USE master", "master", true)]
    [InlineData("DECLARE @x NVARCHAR(10)", "NVARCHAR", false)]
    [InlineData("SELECT ISNULL(a, 0) FROM t", "ISNULL", false)]
    public void 名稱位置看語法樹_型別與函式名稱交給字的名單(string sql, string word, bool isName)
    {
        Assert.Equal(isName, AuditDefinitions.Collect(sql).IsNameReference(At(sql, word, 1)));
    }

    [Fact]
    public void 剖析不過的那一句從錯的詞起到句尾_之前的詞與之後的句子不算()
    {
        const string sql = "DECLARE @n int\nSELECT ReaderId, FROM Lib_Reader WHERE ReaderId = @n\nSELECT @n";
        var definitions = AuditDefinitions.Collect(sql);

        Assert.Equal(1, definitions.UnparsedStatements);
        Assert.False(definitions.IsUnparsed(At(sql, "ReaderId", 1)));
        Assert.True(definitions.IsUnparsed(At(sql, "FROM", 1)));
        Assert.True(definitions.IsUnparsed(At(sql, "@n", 2)));
        Assert.False(definitions.IsUnparsed(At(sql, "SELECT", 2)));
    }

    [Fact]
    public void 挖掉剖析不過的那一句_之後的句子照樣認得名稱()
    {
        const string sql = "SELECT a, FROM Loan\nDECLARE @n int\nSELECT @n";
        var definitions = AuditDefinitions.Collect(sql);

        Assert.True(definitions.IsDefinition(At(sql, "@n", 1)));
        Assert.True(definitions.IsDefinedBefore("@n", At(sql, "@n", 2)));
    }

    [Fact]
    public void 每一句的錯各算一次()
    {
        const string sql = "SELECT a, FROM Loan\nSELECT 1\nUPDATE SET x = 1\nSELECT 2";
        var definitions = AuditDefinitions.Collect(sql);

        Assert.Equal(2, definitions.UnparsedStatements);
        Assert.False(definitions.IsUnparsed(At(sql, "SELECT", 2)));
        Assert.False(definitions.IsUnparsed(At(sql, "SELECT", 3)));
    }

    [Theory]
    [InlineData("SELECT ReaderId FROM Lib_Reader WHERE")]
    [InlineData("IF 1 = 1\nBEGIN\n    SELECT 1 FROM Loan WHERE CopyNo IN (")]
    public void 寫到一半的指令碼錯在結尾_每一格照常稽核(string sql)
    {
        Assert.Equal(0, AuditDefinitions.Collect(sql).UnparsedStatements);
    }

    /// <summary><paramref name="text"/> 在 <paramref name="sql"/> 裡第 <paramref name="occurrence"/> 次出現的位置。</summary>
    private static int At(string sql, string text, int occurrence)
    {
        var index = -1;

        for (var count = 0; count < occurrence; count++)
        {
            index = sql.IndexOf(text, index + 1, System.StringComparison.Ordinal);
        }

        return index;
    }
}
