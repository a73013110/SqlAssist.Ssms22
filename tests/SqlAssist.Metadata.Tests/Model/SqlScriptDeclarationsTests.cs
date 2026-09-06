using System.Linq;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Model;

/// <summary>
/// 拿名稱向這份指令碼換明細。
/// </summary>
/// <remarks>
/// 滑鼠停留（<see cref="SqlObjectLookup"/>）與建議清單的浮動預覽問的是同一件事，
/// 所以共用這一份。兩份實作的症狀是同一個名稱在提示裡有欄位、在預覽裡沒有。
/// </remarks>
public sealed class SqlScriptDeclarationsTests
{
    private const string Script =
        "CREATE TABLE #Loan (CopyNo NVARCHAR(20), ReaderId INT); " +
        "DECLARE @rows TABLE (CopyNo NVARCHAR(20)); " +
        ";WITH c AS (SELECT CopyNo FROM #Loan) SELECT * FROM c";

    [Theory]
    [InlineData("#Loan", SqlObjectKind.TemporaryTable)]
    [InlineData("@rows", SqlObjectKind.TableVariable)]
    [InlineData("c", SqlObjectKind.CommonTableExpression)]
    public void 三種宣告都換得出明細(string name, SqlObjectKind kind)
    {
        var detail = SqlScriptDeclarations.Create(Script).Find(name)!;

        Assert.Equal(kind, detail.Object.Kind);
        Assert.Equal(name, detail.Object.Name);
        Assert.Equal("CopyNo", detail.Columns[0].Name);
    }

    /// <summary>一般名稱不是這份指令碼宣告的，交回 null 讓呼叫端去問中繼資料。</summary>
    [Theory]
    [InlineData("Lib_Reader")]
    [InlineData("#Copy")]
    [InlineData("@readerId")]
    [InlineData("")]
    public void 不是宣告就交回null(string name)
    {
        Assert.Null(SqlScriptDeclarations.Create(Script).Find(name));
    }

    /// <summary>詞法單元傳得進去，呼叫端不必為了問名冊把整份文字再掃一遍。</summary>
    [Fact]
    public void 可以沿用呼叫端已經掃好的詞法單元()
    {
        var declarations = SqlScriptDeclarations.Create(Script, SqlTokenizer.Tokenize(Script));

        Assert.Equal(
            new[] { "CopyNo", "ReaderId" },
            declarations.Find("#Loan")!.Columns.Select(column => column.Name));
    }

    /// <summary>指令碼分頁要的是宣告原文，所以文字與位置要對得起來。</summary>
    [Fact]
    public void 明細帶著宣告原文()
    {
        Assert.Equal(
            "CREATE TABLE #Loan (CopyNo NVARCHAR(20), ReaderId INT)",
            SqlScriptDeclarations.Create(Script).Find("#Loan")!.Definition);
    }

    /// <summary>
    /// <c>SELECT … INTO #tmp</c> 沒有資料行定義，資料行仍然讀得出來。
    /// </summary>
    /// <remarks>
    /// 那份名單就寫在選取清單裡，與 CTE 是同一條推理。少了這一條的症狀是使用者
    /// 停在自己上一行才建立的 <c>#Copy</c> 上，什麼都不顯示。
    /// </remarks>
    [Fact]
    public void SELECT_INTO建立的暫存資料表也換得出明細()
    {
        const string script = "SELECT CopyNo, ReaderId INTO #Copy FROM dbo.Loan;";
        var detail = SqlScriptDeclarations.Create(script).Find("#Copy")!;

        Assert.Equal(SqlObjectKind.TemporaryTable, detail.Object.Kind);
        Assert.Equal(new[] { "CopyNo", "ReaderId" }, detail.Columns.Select(column => column.Name));

        // 指令碼分頁交出的是原文；這一句本身就執行得動，不必補任何前綴。
        Assert.Equal("SELECT CopyNo, ReaderId INTO #Copy FROM dbo.Loan", detail.Definition);
    }

    /// <summary>
    /// 投影不出資料行時交回 null。
    /// </summary>
    /// <remarks>
    /// <c>SELECT *</c> 的名單只有中繼資料知道。交回一份空的明細會讓提示寫出
    /// 「沒有欄位」，而那是假話；交回 null 讓呼叫端說得出實情——讀不出資料行。
    /// </remarks>
    [Fact]
    public void 投影不出資料行就交回null()
    {
        Assert.Null(SqlScriptDeclarations.Create("SELECT * INTO #Copy FROM dbo.Loan;").Find("#Copy"));
    }

    /// <summary>
    /// 投影出來的欄位插得進去。
    /// </summary>
    /// <remarks>
    /// 這是 <c>INSERT INTO #Copy</c> 提交之後展得開整句的前提：讀不出型別不代表插不
    /// 進去，<see cref="SqlColumnInfo.CanInsert"/> 擋的是 IDENTITY、計算資料行與
    /// <c>rowversion</c>，而投影出來的欄位一種都不是。
    /// </remarks>
    [Fact]
    public void 投影出來的欄位插得進去()
    {
        var detail = SqlScriptDeclarations
            .Create("SELECT CopyNo, ReaderId INTO #Copy FROM dbo.Loan;")
            .Find("#Copy")!;

        Assert.All(detail.Columns, column => Assert.True(column.CanInsert));

        // 型別空字串是實話：值因此走「可為 NULL」那一條，填 NULL。
        Assert.All(detail.Columns, column => Assert.Equal(string.Empty, column.DataType));
        Assert.All(detail.Columns, column => Assert.True(column.IsNullable));
    }

    /// <summary>兩種寫法都有時，帶著型別的那一份說得比較多。</summary>
    [Fact]
    public void 帶型別的宣告優先於SELECT_INTO()
    {
        var detail = SqlScriptDeclarations
            .Create(
                "CREATE TABLE #Copy (CopyNo NVARCHAR(20));" +
                "SELECT ReaderId INTO #Copy FROM dbo.Loan;")
            .Find("#Copy")!;

        Assert.Equal(new[] { "CopyNo" }, detail.Columns.Select(column => column.Name));
        Assert.Equal("NVARCHAR(20)", detail.Columns[0].DataType);
    }

    /// <summary>井號與小老鼠各只有一個意思，一個字元就分得出來。</summary>
    [Theory]
    [InlineData("#Loan", SqlObjectKind.TemporaryTable)]
    [InlineData("##Loan", SqlObjectKind.TemporaryTable)]
    [InlineData("@rows", SqlObjectKind.TableVariable)]
    public void 名稱決定是暫存資料表還是資料表變數(string name, SqlObjectKind kind)
    {
        Assert.Equal(kind, SqlScriptDeclarations.KindOf(name));
    }
}
