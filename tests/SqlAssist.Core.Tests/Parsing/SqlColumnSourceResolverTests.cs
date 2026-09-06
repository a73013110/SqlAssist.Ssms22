using System.Collections.Generic;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

/// <summary>
/// CTE 名冊對外的那一面：滑鼠停留提示與結構預覽的物件定位問的就是這裡。
/// </summary>
/// <remarks>
/// CTE 只存在於這份指令碼裡，中繼資料一列都查不到。定位若只問中繼資料，
/// 症狀是使用者上一行才寫下的名稱，停在上面什麼都不顯示。
/// 名冊與欄位建議、<c>SELECT *</c> 展開共用同一次掃描的結果。
/// </remarks>
public sealed class SqlColumnSourceResolverTests
{
    private static SqlColumnSourceResolver Resolve(string sql) =>
        new(SqlTokenizer.Tokenize(sql));

    /// <summary>把敘述裡的一個別名攤平成欄位來源；解析不出來就是失敗。</summary>
    private static IReadOnlyList<SqlColumnSource> ResolveAlias(string sql, string alias)
    {
        var tokens = SqlTokenizer.Tokenize(sql);

        Assert.True(SqlScopeAnalyzer.Analyze(tokens, sql.Length).TryResolve(alias, out var reference));

        var sources = new SqlColumnSourceResolver(tokens).Resolve(reference);

        Assert.NotNull(sources);
        return sources!;
    }

    [Fact]
    public void 不是CTE的名稱回傳null()
    {
        var resolver = Resolve("SELECT * FROM dbo.Lib_Reader");

        Assert.Null(resolver.FindCommonTableExpression("Lib_Reader"));
    }

    /// <summary>選取清單寫得出名稱時，輸出欄位就是那幾個。</summary>
    [Fact]
    public void 讀出選取清單的輸出欄位()
    {
        var resolver = Resolve(";WITH c AS (SELECT CopyNo, ReaderId FROM dbo.Loan) SELECT * FROM c");
        var cte = resolver.FindCommonTableExpression("c");

        Assert.NotNull(cte);
        Assert.Equal(new[] { "CopyNo", "ReaderId" }, resolver.ResolveCommonTableExpressionColumns(cte!));
    }

    /// <summary>明確寫出的資料行清單覆寫主體算出來的名稱。</summary>
    [Fact]
    public void 資料行清單優先於主體()
    {
        var resolver = Resolve(";WITH c (a, b) AS (SELECT CopyNo, ReaderId FROM dbo.Loan) SELECT * FROM c");

        Assert.Equal(
            new[] { "a", "b" },
            resolver.ResolveCommonTableExpressionColumns(resolver.FindCommonTableExpression("c")!));
    }

    /// <summary>主體的 <c>SELECT *</c> 打在暫存資料表上時，欄位仍然讀得出來。</summary>
    /// <remarks>那份名單也寫在指令碼裡，與 CTE 是同一條推理。</remarks>
    [Fact]
    public void 主體的星號展開到指令碼宣告的資料表()
    {
        var resolver = Resolve(
            "CREATE TABLE #Loan (CopyNo NVARCHAR(20), ReaderId INT);" +
            ";WITH c AS (SELECT * FROM #Loan) SELECT * FROM c");

        Assert.Equal(
            new[] { "CopyNo", "ReaderId" },
            resolver.ResolveCommonTableExpressionColumns(resolver.FindCommonTableExpression("c")!));
    }

    /// <summary>
    /// 主體的 <c>SELECT *</c> 打在資料庫的資料表上時整份放棄。
    /// </summary>
    /// <remarks>
    /// 那份名單只有中繼資料知道，而問這個問題的滑鼠停留路徑不等查詢。
    /// 半份欄位清單看起來與完整的一模一樣，使用者會照著它去找一個沒有列出來的欄位。
    /// </remarks>
    [Fact]
    public void 主體要問中繼資料時交出空清單()
    {
        var resolver = Resolve(";WITH c AS (SELECT Id, * FROM dbo.Loan) SELECT * FROM c");

        Assert.Empty(resolver.ResolveCommonTableExpressionColumns(resolver.FindCommonTableExpression("c")!));
    }

    /// <summary>
    /// 資料表值建構式的輸出欄位就是別名後面那份資料行清單。
    /// </summary>
    /// <remarks>
    /// <c>VALUES</c> 不是 <c>SELECT</c>，主體一個名稱都讀不出來——那份清單是唯一的
    /// 出處。少了它的症狀是 <c>T.</c>、<c>SELECT T.*</c> 與投影出來的暫存資料表
    /// 一起變成空的。
    /// </remarks>
    [Fact]
    public void 資料表值建構式讀別名後面的資料行清單()
    {
        var source = Assert.Single(
            ResolveAlias("SELECT * FROM (VALUES (1, N'Alice')) AS T (CopyNo, ReaderId)", "T"));

        Assert.Equal(SqlColumnSourceKind.Names, source.Kind);
        Assert.Equal(new[] { "CopyNo", "ReaderId" }, source.Names);
        Assert.Equal("T", source.Qualifier);
    }

    /// <summary>
    /// 資料列集函式的輸出欄位也是別名後面那份資料行清單。
    /// </summary>
    /// <remarks>
    /// 那是它唯一的出處：連結伺服器上那句查詢的結果，中繼資料一列都答不出來。
    /// </remarks>
    [Fact]
    public void 資料列集函式讀別名後面的資料行清單()
    {
        var source = Assert.Single(
            ResolveAlias("SELECT * FROM OPENQUERY(LibArchive, 'SELECT 1, 2') AS T (CopyNo, ReaderId)", "T"));

        Assert.Equal(SqlColumnSourceKind.Names, source.Kind);
        Assert.Equal(new[] { "CopyNo", "ReaderId" }, source.Names);
    }

    /// <summary>
    /// 衍生資料表寫出來的資料行清單覆寫主體算出來的名稱。
    /// </summary>
    /// <remarks>與上面的 CTE 是同一條規則，因此走同一份實作。</remarks>
    [Fact]
    public void 衍生資料表的資料行清單優先於主體()
    {
        var source = Assert.Single(
            ResolveAlias("SELECT * FROM (SELECT Id, Title FROM dbo.Copy) AS T (CopyNo, ReaderId)", "T"));

        Assert.Equal(new[] { "CopyNo", "ReaderId" }, source.Names);
    }

    /// <summary>
    /// 宣告在原文裡的範圍不含前面的 <c>WITH</c> 與逗號。
    /// </summary>
    /// <remarks>
    /// 結構預覽交出去的是這一段原文；重組一份出來會失真。
    /// </remarks>
    [Fact]
    public void 記下宣告在原文裡的範圍()
    {
        const string sql = ";WITH a AS (SELECT 1 AS Id), b AS (SELECT 2 AS Id) SELECT * FROM b";
        var resolver = Resolve(sql);
        var second = resolver.FindCommonTableExpression("b")!;

        Assert.Equal("b AS (SELECT 2 AS Id)", sql.Substring(second.Start, second.End - second.Start));
    }
}
