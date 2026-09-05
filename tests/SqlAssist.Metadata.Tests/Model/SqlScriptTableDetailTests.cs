using System.Linq;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Model;

/// <summary>
/// 指令碼裡讀出來的資料表宣告換成中繼資料層的物件描述。
/// </summary>
/// <remarks>
/// 換過來的目的只有一個：不要有第二份「哪些欄位插得進去」。
/// </remarks>
public sealed class SqlScriptTableDetailTests
{
    private static SqlObjectDetail Create(string sql, string name, string? script = null)
    {
        var tables = SqlScriptTableCollector.Collect(SqlTokenizer.Tokenize(sql));

        Assert.True(tables.TryGetValue(name, out var table), $"沒有讀出 {name} 的宣告。");
        return SqlScriptTableDetail.Create(table!, script);
    }

    /// <summary>
    /// IDENTITY 與計算資料行插不進去，走的是資料庫物件同一條規則。
    /// </summary>
    /// <remarks>
    /// 漏掉任何一種的症狀相同——展開出來的 <c>INSERT</c> 一執行就錯。
    /// </remarks>
    [Fact]
    public void 插不進去的資料行照同一條規則排除()
    {
        var detail = Create(
            "CREATE TABLE #Loan (" +
            "Id INT IDENTITY(1,1) PRIMARY KEY, " +
            "CopyNo NVARCHAR(20) NOT NULL, " +
            "Total AS 1 * 2, " +
            "Stamp ROWVERSION)",
            "#Loan");

        Assert.Equal(
            new[] { "CopyNo" },
            detail.Columns.Where(column => column.CanInsert).Select(column => column.Name));
    }

    [Fact]
    public void 資料行的順序與性質照宣告帶過來()
    {
        var detail = Create(
            "DECLARE @Loan TABLE (" +
            "Id INT IDENTITY(1,1) PRIMARY KEY, " +
            "CopyNo NVARCHAR(20) NOT NULL, " +
            "ReaderId INT NULL, " +
            "LoanDate DATETIME NOT NULL DEFAULT GETDATE())",
            "@Loan");

        Assert.Equal(
            new[] { "Id", "CopyNo", "ReaderId", "LoanDate" },
            detail.Columns.Select(column => column.Name));

        Assert.Equal(new[] { 1, 2, 3, 4 }, detail.Columns.Select(column => column.Ordinal));
        Assert.True(detail.Columns[0].IsPrimaryKey);
        Assert.Equal("NVARCHAR(20)", detail.Columns[1].DataType);
        Assert.True(detail.Columns[2].IsNullable);
        Assert.False(string.IsNullOrEmpty(detail.Columns[3].DefaultDefinition));
    }

    /// <summary>
    /// 沒有結構描述，也沒有 object_id。
    /// </summary>
    /// <remarks>
    /// 這兩種名稱在 <c>sys.objects</c> 裡查不到，硬填一個 <c>dbo</c> 只會讓紀錄檔
    /// 說謊——而 <c>[dbo].[@Loan]</c> 連文法都不成立。
    /// </remarks>
    [Theory]
    [InlineData("CREATE TABLE #Loan (Id INT)", "#Loan", SqlObjectKind.TemporaryTable)]
    [InlineData("DECLARE @Loan TABLE (Id INT)", "@Loan", SqlObjectKind.TableVariable)]
    public void 沒有結構描述也沒有物件識別碼(string sql, string name, SqlObjectKind kind)
    {
        var info = Create(sql, name).Object;

        Assert.Equal(0, info.ObjectId);
        Assert.Equal(string.Empty, info.SchemaName);
        Assert.Equal(name, info.QualifiedName);
        Assert.Equal(kind, info.Kind);

        // 編號是 0，種類必須說得出它其實不在中繼資料裡——中繼資料的第二、三層
        // 快取正是照編號存的，放行的症狀是兩個暫存資料表互相蓋掉對方的欄位。
        Assert.True(info.Kind.IsScriptDeclared());
        Assert.False(info.Kind.HasCatalogColumns());
    }

    /// <summary>
    /// 指令碼分頁交出去的是宣告原文，不是由資料行重組的一份。
    /// </summary>
    /// <remarks>
    /// 重組會失真：文字讀得出「有沒有 DEFAULT」，讀不出它寫的是什麼，
    /// 重組出來的那一段會寫著 <c>DEFAULT (指令碼宣告)</c>，貼回編輯器執行不了。
    /// </remarks>
    [Fact]
    public void 暫存資料表的指令碼就是宣告原文()
    {
        const string script = "SELECT 1; CREATE TABLE #Loan (Id INT DEFAULT (0)); SELECT * FROM #Loan;";
        var structure = new SqlObjectStructure(Create(script, "#Loan", script));

        Assert.True(structure.CanBuildExecutableScript);
        Assert.Equal("CREATE TABLE #Loan (Id INT DEFAULT (0))", structure.BuildScript());
    }

    /// <summary>
    /// 資料表變數補上 <c>DECLARE</c>：認得的是「變數 TABLE (」這個形狀本身，
    /// 而 <c>RETURNS @rows TABLE (…)</c> 的原文不是從 <c>DECLARE</c> 開始的。
    /// </summary>
    [Fact]
    public void 資料表變數的指令碼補上宣告關鍵字()
    {
        const string script = "CREATE FUNCTION dbo.fn_X() RETURNS @rows TABLE (Id INT) AS BEGIN RETURN END";
        var structure = new SqlObjectStructure(Create(script, "@rows", script));

        Assert.Equal("DECLARE @rows TABLE (Id INT)", structure.BuildScript());
    }

    /// <summary>
    /// CTE 只讀得出欄位名稱：型別、NULL 與 PK 要追到最內層的資料表，
    /// 而中間任何一段運算式都會讓答案不成立。
    /// </summary>
    [Fact]
    public void CTE只帶名稱且指令碼是宣告原文()
    {
        const string script = ";WITH c AS (SELECT Id, CopyNo FROM dbo.Loan) SELECT * FROM c";
        var resolver = new SqlColumnSourceResolver(SqlTokenizer.Tokenize(script));
        var cte = resolver.FindCommonTableExpression("c");

        Assert.NotNull(cte);

        var detail = SqlScriptTableDetail.Create(
            cte!,
            resolver.ResolveCommonTableExpressionColumns(cte!),
            script);

        Assert.Equal(SqlObjectKind.CommonTableExpression, detail.Object.Kind);
        Assert.Equal(new[] { "Id", "CopyNo" }, detail.Columns.Select(column => column.Name));

        // 型別、NULL 與 PK 一個都不知道；空字串在這裡不是遺漏而是實話，
        // 而預設的可為 NULL 剛好不會在提示上標出任何徽章。
        Assert.All(detail.Columns, column => Assert.Equal(string.Empty, column.DataType));
        Assert.All(detail.Columns, column => Assert.Empty(SqlColumnPresentation.Flags(column)));

        // CTE 的宣告不是一句可以單獨執行的敘述，因此不算可執行指令碼；
        // 預覽仍然給得出使用者眼前的那一段原文。
        Assert.False(detail.Object.Kind.HasExecutableScript());
        Assert.Equal("WITH c AS (SELECT Id, CopyNo FROM dbo.Loan)", detail.BuildPreview());
    }
}
