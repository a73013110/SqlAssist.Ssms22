using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

/// <summary>
/// Ctrl＋點擊時，哪些字底下會出現底線、底線蓋到哪裡。
/// </summary>
/// <remarks>
/// 錯在寬的那一側只是多一句「不是可辨識的資料庫物件」；錯在窄的那一側是欄位名稱點不動，
/// 所以只擋一定不是物件的兩種：保留字與內建名稱。底線蓋住的是點下去會打開的那一整個東西。
/// </remarks>
public sealed class SqlClickTargetTests
{
    /// <returns>連結蓋住的那段文字；沒有連結時為 null。</returns>
    private static string? LinkAtMarker(string textWithMarker, bool includeBuiltIns = true)
    {
        var input = SqlWithCaret.Parse(textWithMarker);
        var text = input.Text;
        var lineStart = text.LastIndexOf('\n', input.Caret == 0 ? 0 : input.Caret - 1) + 1;
        var lineEnd = text.IndexOf('\n', input.Caret);
        var line = text.Substring(lineStart, (lineEnd < 0 ? text.Length : lineEnd) - lineStart);

        return SqlClickTarget.FindAt(line, input.Caret - lineStart, includeBuiltIns, () => text, lineStart) is { } span
            ? line.Substring(span.Start, span.Length)
            : null;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 物件名稱兩種動作都能點(bool includeBuiltIns)
    {
        Assert.Equal("Lib_Reader", LinkAtMarker("SELECT * FROM Lib|_Reader", includeBuiltIns));
    }

    /// <summary>結構描述、資料庫那幾段只是在說物件在哪裡：點哪一段都是同一個物件，所以是同一條底線。</summary>
    [Theory]
    [InlineData("SELECT * FROM dbo.Lib|_Reader", "dbo.Lib_Reader")]
    [InlineData("SELECT * FROM d|bo.Lib_Reader", "dbo.Lib_Reader")]
    [InlineData("SELECT * FROM [d|bo].[Lib_Reader] r", "[dbo].[Lib_Reader]")]
    [InlineData("SELECT * FROM LibArchive.d|bo.Loan", "LibArchive.dbo.Loan")]
    [InlineData("SELECT * FROM Lib|Archive..Loan", "LibArchive..Loan")]
    [InlineData("EXEC d|bo.usp_Renew 1", "dbo.usp_Renew")]
    [InlineData("SELECT *\nFROM d|bo.Loan l\nWHERE l.CopyNo = 1", "dbo.Loan")]
    public void 限定名稱整串都是連結(string text, string expected)
    {
        Assert.Equal(expected, LinkAtMarker(text));
    }

    /// <summary>
    /// 結構描述與表名大小寫不分地相同時，那一段仍是結構描述：它是這個來源自己的名稱，
    /// 不是在用這個來源的公開名稱。
    /// </summary>
    [Theory]
    [InlineData("SELECT * FROM lo|an.Loan")]
    [InlineData("SELECT * FROM loan.Lo|an")]
    public void 與表名同名的結構描述仍是名稱的一部分(string text)
    {
        Assert.Equal("loan.Loan", LinkAtMarker(text));
    }

    /// <summary>別名那一段問的是那張表、欄位那一段問的是那個欄位，兩個答案各一條底線。</summary>
    [Theory]
    [InlineData("SELECT l|.CopyNo FROM dbo.Loan l", "l")]
    [InlineData("SELECT l.Copy|No FROM dbo.Loan l", "l.CopyNo")]
    [InlineData("SELECT Lo|an.CopyNo FROM dbo.Loan", "Loan")]
    [InlineData("SELECT l.CopyNo\nFROM dbo.Loan l\nWHERE l|.Branch = 1", "l")]
    public void 別名與欄位各自是連結(string text, string expected)
    {
        Assert.Equal(expected, LinkAtMarker(text));
    }

    [Theory]
    [InlineData("SEL|ECT * FROM Lib_Reader")]
    [InlineData("SELECT * FR|OM Lib_Reader")]
    [InlineData("SELECT * FROM Lib_Reader WH|ERE 1 = 1")]
    public void 保留字不是連結(string text)
    {
        Assert.Null(LinkAtMarker(text));
    }

    /// <summary>加了括號的保留字就是合法的名稱。</summary>
    [Fact]
    public void 加括號的保留字是連結()
    {
        Assert.Equal("[Order]", LinkAtMarker("SELECT [Or|der] FROM Loan"));
    }

    /// <summary>非保留的關鍵字常被拿來當欄位名稱，不能擋。</summary>
    [Fact]
    public void 非保留關鍵字仍是連結()
    {
        Assert.Equal("Type", LinkAtMarker("SELECT Ty|pe FROM Cat_BookCopy"));
    }

    [Fact]
    public void 內建名稱只給看得懂它的動作()
    {
        Assert.Equal("CONVERT", LinkAtMarker("SELECT CON|VERT(varchar(10), GETDATE(), 112)", includeBuiltIns: true));
        Assert.Null(LinkAtMarker("SELECT CON|VERT(varchar(10), GETDATE(), 112)", includeBuiltIns: false));
    }

    /// <summary>多字的語句是一份說明：停在哪一個字上都是同一條底線。</summary>
    [Theory]
    [InlineData("AL|TER TABLE dbo.Loan ADD Note nvarchar(100)", "ALTER TABLE")]
    [InlineData("ALTER TA|BLE dbo.Loan ADD Note nvarchar(100)", "ALTER TABLE")]
    [InlineData("BULK INS|ERT dbo.Loan FROM 'loan.csv'", "BULK INSERT")]
    public void 多字語句是一條連結(string text, string expected)
    {
        Assert.Equal(expected, LinkAtMarker(text));
    }

    /// <summary>系統程序的限定字與名稱同一份說明。</summary>
    [Theory]
    [InlineData("EXEC s|ys.sp_help 'dbo.Loan'")]
    [InlineData("EXEC sys.sp_he|lp 'dbo.Loan'")]
    public void 限定的系統程序是一條連結(string text)
    {
        Assert.Equal("sys.sp_help", LinkAtMarker(text));
    }

    [Theory]
    [InlineData("SELECT 'Lib|_Reader'")]
    [InlineData("-- Lib|_Reader")]
    public void 字串與註解裡不是連結(string text)
    {
        Assert.Null(LinkAtMarker(text));
    }
}
