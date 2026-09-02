using System.Linq;
using SqlAssist.Core.Parsing;
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
    private static SqlObjectDetail Create(string sql, string name)
    {
        var tables = SqlScriptTableCollector.Collect(SqlTokenizer.Tokenize(sql));

        Assert.True(tables.TryGetValue(name, out var table), $"沒有讀出 {name} 的宣告。");
        return SqlScriptTableDetail.Create(table!);
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
    [InlineData("CREATE TABLE #Loan (Id INT)", "#Loan")]
    [InlineData("DECLARE @Loan TABLE (Id INT)", "@Loan")]
    public void 沒有結構描述也沒有物件識別碼(string sql, string name)
    {
        var info = Create(sql, name).Object;

        Assert.Equal(0, info.ObjectId);
        Assert.Equal(string.Empty, info.SchemaName);
        Assert.Equal(name, info.QualifiedName);
        Assert.Equal(SqlObjectKind.Table, info.Kind);
    }
}
