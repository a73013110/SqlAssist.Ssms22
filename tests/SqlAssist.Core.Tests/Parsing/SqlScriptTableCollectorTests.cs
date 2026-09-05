using System.Linq;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

/// <summary>
/// 從指令碼讀出暫存資料表與資料表變數的資料行清單。
/// </summary>
/// <remarks>
/// 這兩種名稱中繼資料一列都查不到——資料表變數不是 <c>sys.objects</c> 裡的物件，
/// 暫存資料表在 tempdb 裡。少了這一份的症狀是欄位建議、<c>SELECT *</c> 展開與
/// <c>INSERT</c> 骨架三個功能同時對它們失效。
/// </remarks>
public sealed class SqlScriptTableCollectorTests
{
    private const string TemporaryTable =
        "CREATE TABLE #Loan\r\n" +
        "(\r\n" +
        "    Id            INT IDENTITY(1,1) PRIMARY KEY,\r\n" +
        "    CopyNo        NVARCHAR(20)  NOT NULL,\r\n" +
        "    ReaderId      INT           NULL,\r\n" +
        "    Fine          DECIMAL(18,2) NOT NULL DEFAULT 0,\r\n" +
        "    LoanDate      DATETIME      NOT NULL DEFAULT GETDATE()\r\n" +
        ");";

    private static SqlScriptTable Collect(string sql, string name)
    {
        var tables = SqlScriptTableCollector.Collect(SqlTokenizer.Tokenize(sql));

        Assert.True(tables.TryGetValue(name, out var table), $"沒有讀出 {name} 的宣告。");
        return table!;
    }

    [Fact]
    public void 讀出暫存資料表的資料行順序()
    {
        Assert.Equal(
            new[] { "Id", "CopyNo", "ReaderId", "Fine", "LoanDate" },
            Collect(TemporaryTable, "#Loan").ColumnNames);
    }

    /// <summary>型別照原文帶走，長度與有效位數都要在。</summary>
    /// <remarks>
    /// 詞法單元不帶空白，串起來的規則錯一次就會得到 <c>NVARCHAR ( 20 )</c>
    /// 或 <c>DECIMAL(18 ,2)</c>——那會原封不動印進展開後的註解裡。
    /// </remarks>
    [Fact]
    public void 型別照原文帶走()
    {
        Assert.Equal(
            new[] { "INT", "NVARCHAR(20)", "INT", "DECIMAL(18,2)", "DATETIME" },
            Collect(TemporaryTable, "#Loan").Columns.Select(column => column.DataType));
    }

    [Fact]
    public void 讀出可為NULL與預設值()
    {
        var columns = Collect(TemporaryTable, "#Loan").Columns;

        Assert.False(columns[1].IsNullable);
        Assert.True(columns[2].IsNullable);
        Assert.True(columns[3].HasDefault);
        Assert.True(columns[4].HasDefault);
        Assert.False(columns[1].HasDefault);
    }

    /// <remarks>
    /// IDENTITY 的括號要整組跳過，否則 <c>(1,1)</c> 裡的東西會被當成資料行選項。
    /// 主索引鍵一律不可為 NULL——留著的話展開出來的 <c>VALUES</c> 會替它填
    /// <c>NULL</c>，一執行就錯。
    /// </remarks>
    [Fact]
    public void 讀出IDENTITY與主索引鍵()
    {
        var identity = Collect(TemporaryTable, "#Loan").Columns[0];

        Assert.True(identity.IsIdentity);
        Assert.True(identity.IsPrimaryKey);
        Assert.False(identity.IsNullable);
        Assert.Equal("INT", identity.DataType);
    }

    /// <summary>資料表層級的 <c>PRIMARY KEY (…)</c> 寫在資料行後面，兩趟才對得起來。</summary>
    [Theory]
    [InlineData("CREATE TABLE #Copy (CopyNo NVARCHAR(20), Title NVARCHAR(100), PRIMARY KEY (CopyNo))")]
    [InlineData("CREATE TABLE #Copy (CopyNo NVARCHAR(20), Title NVARCHAR(100), " +
        "CONSTRAINT PK_Copy PRIMARY KEY CLUSTERED (CopyNo ASC))")]
    public void 讀出資料表層級的主索引鍵(string sql)
    {
        var columns = Collect(sql, "#Copy").Columns;

        Assert.True(columns[0].IsPrimaryKey);
        Assert.False(columns[0].IsNullable);
        Assert.False(columns[1].IsPrimaryKey);
    }

    /// <summary>資料表變數的宣告形狀與暫存資料表不同，讀出來的東西一樣。</summary>
    /// <remarks>
    /// 認的是「變數 TABLE (」這個形狀本身，因此函式的
    /// <c>RETURNS @rows TABLE (…)</c> 免費一起認得。
    /// </remarks>
    [Theory]
    [InlineData("DECLARE @Loan TABLE (Id INT IDENTITY(1,1), CopyNo NVARCHAR(20) NOT NULL);")]
    [InlineData("CREATE FUNCTION dbo.fnLoans() RETURNS @Loan TABLE " +
        "(Id INT IDENTITY(1,1), CopyNo NVARCHAR(20) NOT NULL) AS BEGIN RETURN END")]
    public void 讀出資料表變數(string sql)
    {
        var table = Collect(sql, "@Loan");

        Assert.Equal(new[] { "Id", "CopyNo" }, table.ColumnNames);
        Assert.True(table.Columns[0].IsIdentity);
    }

    /// <summary><c>DECLARE @a INT, @b TABLE (…)</c> 裡的一般變數不會混進來。</summary>
    [Fact]
    public void 只收資料表形狀的宣告()
    {
        var tables = SqlScriptTableCollector.Collect(
            SqlTokenizer.Tokenize("DECLARE @readerId INT, @Loan TABLE (CopyNo NVARCHAR(20));"));

        Assert.Equal(new[] { "@Loan" }, tables.Keys);
    }

    /// <summary>
    /// <c>INSERT INTO #tmp (a, b)</c> 不是宣告。
    /// </summary>
    /// <remarks>
    /// 它的形狀與資料行清單一模一樣，少了 <c>CREATE TABLE</c> 這道前綴就會被讀成
    /// 一份「每個資料行都沒有型別」的宣告，而那份假宣告會蓋掉真正的那一份。
    /// </remarks>
    [Fact]
    public void INSERT的資料行清單不是宣告()
    {
        var table = Collect(
            TemporaryTable + "\r\nINSERT INTO #Loan (CopyNo, ReaderId) VALUES ('C1', 1);",
            "#Loan");

        Assert.Equal(5, table.Columns.Count);
        Assert.Equal("NVARCHAR(20)", table.Columns[1].DataType);
    }

    /// <summary>
    /// 記下宣告在原文裡的範圍。
    /// </summary>
    /// <remarks>
    /// 結構預覽的指令碼分頁交出去的是這一段原文。由讀出來的資料行重組一份會失真：
    /// 文字讀得出「有沒有 DEFAULT」，卻讀不出它寫的是什麼。
    /// </remarks>
    [Theory]
    [InlineData("SELECT 1; CREATE TABLE #Loan (CopyNo NVARCHAR(20));", "#Loan",
        "CREATE TABLE #Loan (CopyNo NVARCHAR(20))")]
    [InlineData("DECLARE @readerId INT, @Loan TABLE (CopyNo NVARCHAR(20));", "@Loan",
        "@Loan TABLE (CopyNo NVARCHAR(20))")]
    public void 記下宣告在原文裡的範圍(string sql, string name, string expected)
    {
        var table = Collect(sql, name);

        Assert.Equal(expected, sql.Substring(table.Start, table.End - table.Start));
    }

    /// <summary>一般資料表不收：它在中繼資料裡，那一份才是現在的樣子。</summary>
    [Fact]
    public void 一般資料表不收()
    {
        Assert.Empty(SqlScriptTableCollector.Collect(
            SqlTokenizer.Tokenize("CREATE TABLE dbo.Loan (CopyNo NVARCHAR(20));")));
    }

    /// <summary>計算資料行插不進去，型別也推不出來。</summary>
    [Fact]
    public void 計算資料行只記下是計算資料行()
    {
        var computed = Collect(
            "CREATE TABLE #Loan (Fine DECIMAL(18,2), Total AS Fine * 2)",
            "#Loan").Columns[1];

        Assert.True(computed.IsComputed);
        Assert.Equal(string.Empty, computed.DataType);
    }

    /// <summary>
    /// 括號還沒關起來時當它不存在。
    /// </summary>
    /// <remarks>
    /// 使用者正在打這份宣告，讀到一半的資料行清單沒有一個是完整的。
    /// 打完之後下一次按鍵就有了。
    /// </remarks>
    [Fact]
    public void 括號還沒關起來就不收()
    {
        Assert.Empty(SqlScriptTableCollector.Collect(
            SqlTokenizer.Tokenize("CREATE TABLE #Loan (CopyNo NVARCHAR(20),")));
    }

    /// <summary>全域暫存資料表與方括號寫法都是同一件事。</summary>
    [Theory]
    [InlineData("CREATE TABLE ##Loan (CopyNo NVARCHAR(20))", "##Loan")]
    [InlineData("CREATE TABLE [#Loan] (CopyNo NVARCHAR(20))", "#Loan")]
    public void 認得全域暫存表與方括號寫法(string sql, string name)
    {
        Assert.Equal(new[] { "CopyNo" }, Collect(sql, name).ColumnNames);
    }
}
