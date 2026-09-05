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
