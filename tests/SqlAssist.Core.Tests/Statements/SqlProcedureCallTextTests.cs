using System.Collections.Generic;
using SqlAssist.Core.Statements;
using Xunit;

namespace SqlAssist.Core.Tests.Statements;

public sealed class SqlProcedureCallTextTests
{
    private static readonly SqlStatementParameter[] Renew =
    {
        new("@LoanId", "int", isOutput: false, isOptional: false),
        new("@Days", "int", isOutput: false, isOptional: true),
        new("@NewDueDate", "datetime2(7)", isOutput: true, isOptional: false)
    };

    private static string Build(
        IReadOnlyList<SqlStatementParameter> parameters,
        out int caretOffset,
        string indent = "",
        string keyword = "EXEC")
    {
        return SqlProcedureCallText.Build(
            keyword,
            "dbo.usp_Loan_Renew",
            parameters,
            indent,
            "\r\n",
            out caretOffset);
    }

    /// <remarks>
    /// 續行對齊到第一個參數所在的欄，每一列的 @ 因此落在同一個位置：
    /// 掃過去就知道有幾個參數、少填了哪一個。
    /// </remarks>
    [Fact]
    public void 續行對齊到第一個參數並補上OUTPUT的宣告()
    {
        var text = Build(Renew, out _);

        Assert.Equal(
            "DECLARE @NewDueDate datetime2(7);\r\n" +
            "EXEC dbo.usp_Loan_Renew @LoanId = 0,                     -- int\r\n" +
            "                        @Days = 0,                       -- int，選擇性\r\n" +
            "                        @NewDueDate = @NewDueDate OUTPUT -- datetime2(7)",
            text);
    }

    /// <remarks>
    /// OUTPUT 參數傳的必須是變數，光給字面值是語法錯誤；少了 DECLARE，
    /// 展開出來的東西連編譯都過不了。
    /// </remarks>
    [Fact]
    public void 沒有OUTPUT參數時不寫DECLARE()
    {
        var text = Build(
            new[] { new SqlStatementParameter("@ReaderId", "int", isOutput: false, isOptional: false) },
            out _);

        Assert.Equal("EXEC dbo.usp_Loan_Renew @ReaderId = 0 -- int", text);
    }

    [Fact]
    public void 游標停在第一個參數的值上()
    {
        var text = Build(Renew, out var caretOffset);

        Assert.Equal("0,", text.Substring(caretOffset, 2));
    }

    /// <remarks>
    /// 統一改寫成 EXEC 也合法，但那是使用者沒有要求的改動——與展開萬用字元時
    /// 保留他自己寫的限定字是同一條。
    /// </remarks>
    [Theory]
    [InlineData("EXEC")]
    [InlineData("EXECUTE")]
    [InlineData("exec")]
    public void 照原文帶回EXEC關鍵字(string keyword)
    {
        var text = Build(
            new[] { new SqlStatementParameter("@ReaderId", "int", isOutput: false, isOptional: false) },
            out _,
            keyword: keyword);

        Assert.StartsWith(keyword + " dbo.usp_Loan_Renew ", text);
    }

    /// <remarks>
    /// 縮排裡有定位字元時，續行只補「EXEC 名稱 」那一段的寬度：
    /// 一個定位字元只算一個字元，把它算進續行的空白數就會歪掉。
    /// </remarks>
    [Fact]
    public void 定位字元縮排原樣重複()
    {
        var text = Build(
            new[]
            {
                new SqlStatementParameter("@ReaderId", "int", isOutput: false, isOptional: false),
                new SqlStatementParameter("@TagId", "int", isOutput: false, isOptional: false)
            },
            out _,
            indent: "\t");

        Assert.Equal(
            "EXEC dbo.usp_Loan_Renew @ReaderId = 0, -- int\r\n" +
            "\t                        @TagId = 0     -- int",
            text);
    }
}
