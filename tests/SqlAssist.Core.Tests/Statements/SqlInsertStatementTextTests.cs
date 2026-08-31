using System.Collections.Generic;
using SqlAssist.Core.Statements;
using Xunit;

namespace SqlAssist.Core.Tests.Statements;

public sealed class SqlInsertStatementTextTests
{
    private static readonly SqlStatementColumn[] BookCopy =
    {
        new("CopyNo", "varchar(10)", isNullable: false, hasDefault: false),
        new("Barcode", "nvarchar(100)", isNullable: false, hasDefault: true),
        new("BranchId", "int", isNullable: true, hasDefault: false)
    };

    private static string Build(
        IReadOnlyList<SqlStatementColumn> columns,
        out int caretOffset,
        string indent = "")
    {
        return SqlInsertStatementText.Build(
            "dbo.Cat_BookCopy",
            columns,
            indent,
            "\r\n",
            out caretOffset);
    }

    [Fact]
    public void 欄位與值上下成對且註解對齊()
    {
        var text = Build(BookCopy, out _);

        Assert.Equal(
            "INSERT INTO dbo.Cat_BookCopy\r\n" +
            "(\r\n" +
            "    CopyNo,\r\n" +
            "    Barcode,\r\n" +
            "    BranchId\r\n" +
            ")\r\n" +
            "VALUES\r\n" +
            "(\r\n" +
            "    '',      -- CopyNo - varchar(10)\r\n" +
            "    DEFAULT, -- Barcode - nvarchar(100)\r\n" +
            "    NULL     -- BranchId - int\r\n" +
            ")",
            text);
    }

    /// <remarks>
    /// 三條的順序不能對調：<c>VALUES (DEFAULT)</c> 對「沒有預設值而且 NOT NULL」的
    /// 欄位是執行期錯誤，所以有預設值才給 DEFAULT，其次才輪到 NULL 與型別預留值。
    /// </remarks>
    [Theory]
    [InlineData(false, true, "DEFAULT")]
    [InlineData(true, true, "DEFAULT")]
    [InlineData(true, false, "NULL")]
    [InlineData(false, false, "''")]
    public void 值的挑選順序是預設值再NULL再型別(bool isNullable, bool hasDefault, string expected)
    {
        var text = Build(
            new[] { new SqlStatementColumn("PUBL_CODE", "varchar(10)", isNullable, hasDefault) },
            out _);

        Assert.Contains($"    {expected} -- PUBL_CODE - varchar(10)", text);
    }

    /// <remarks>展開之後使用者要做的第一件事就是填第一個值。</remarks>
    [Fact]
    public void 游標停在第一個值上()
    {
        var text = Build(BookCopy, out var caretOffset);

        Assert.Equal("''", text.Substring(caretOffset, 2));
    }

    /// <remarks>
    /// 縮排整段重複到每一行，定位字元原樣保留——這一段每一行放的都是同一串字元，
    /// 在定位寬度不是 4 的機器上也對得齊。
    /// </remarks>
    [Fact]
    public void 縮排重複到每一行()
    {
        var text = Build(
            new[] { new SqlStatementColumn("LoanId", "int", isNullable: false, hasDefault: false) },
            out _,
            indent: "\t");

        Assert.Equal(
            "INSERT INTO dbo.Cat_BookCopy\r\n" +
            "\t(\r\n" +
            "\t    LoanId\r\n" +
            "\t)\r\n" +
            "\tVALUES\r\n" +
            "\t(\r\n" +
            "\t    0 -- LoanId - int\r\n" +
            "\t)",
            text);
    }

    /// <remarks>
    /// 只有一個欄位時它同時是第一列與最後一列，逗號與對齊都不能因此算錯。
    /// </remarks>
    [Fact]
    public void 單一欄位不留逗號()
    {
        var text = Build(
            new[] { new SqlStatementColumn("Sort", "int", isNullable: true, hasDefault: false) },
            out _);

        Assert.Contains("    Sort\r\n)", text);
        Assert.Contains("    NULL -- Sort - int", text);
    }
}
