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
