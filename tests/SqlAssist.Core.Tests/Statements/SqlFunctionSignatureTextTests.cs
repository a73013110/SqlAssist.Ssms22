using System;
using System.Collections.Generic;
using SqlAssist.Core.Statements;
using Xunit;

namespace SqlAssist.Core.Tests.Statements;

public sealed class SqlFunctionSignatureTextTests
{
    private static readonly SqlStatementParameter[] LoansByReader =
    {
        new("@readerId", "int", isOutput: false, isOptional: false),
        new("@since", "datetime2(7)", isOutput: false, isOptional: true)
    };

    [Fact]
    public void 排成一行的名稱型別與傳回型別()
    {
        var text = SqlFunctionSignatureText.Build("dbo.fn_DueDate", LoansByReader, "date");

        Assert.Equal("dbo.fn_DueDate(@readerId int, @since datetime2(7)) RETURNS date", text.Content);
    }

    /// <remarks>傳回型別讀不到（資料表值函式）時整段 RETURNS 不寫，不留一個空詞。</remarks>
    [Fact]
    public void 沒有傳回型別就不寫RETURNS()
    {
        Assert.Equal(
            "dbo.fn_Loans(@readerId int, @since datetime2(7))",
            SqlFunctionSignatureText.Build("dbo.fn_Loans", LoansByReader).Content);
    }

    /// <remarks>
    /// 平台要靠這一段把目前的引數標成粗體；用 IndexOf 找回去的話，
    /// 同名或同型別的參數會找到前面那一個。
    /// </remarks>
    [Fact]
    public void 每個參數都算得出自己在那一行的位置()
    {
        var text = SqlFunctionSignatureText.Build("dbo.fn_Between", new[]
        {
            new SqlStatementParameter("@d", "date", isOutput: false, isOptional: false),
            new SqlStatementParameter("@d2", "date", isOutput: false, isOptional: false)
        });

        Assert.Collection(
            text.Parameters,
            first => Assert.Equal("@d date", text.Content.Substring(first.Start, first.Length)),
            second => Assert.Equal("@d2 date", text.Content.Substring(second.Start, second.Length)));
    }

    /// <remarks>
    /// 函式沒有「整個省略」這回事，省略的寫法是 DEFAULT 這個關鍵字，位置照留——
    /// 所以這裡的字與 EXEC 骨架的「選擇性」不同。
    /// </remarks>
    [Fact]
    public void 有預設值的參數說得出可以寫DEFAULT()
    {
        var text = SqlFunctionSignatureText.Build("dbo.fn_DueDate", LoansByReader, "date");

        Assert.Equal("int", text.Parameters[0].Documentation);
        Assert.Equal("datetime2(7)，可寫 DEFAULT", text.Parameters[1].Documentation);
    }

    [Fact]
    public void 名稱為空時丟出例外()
    {
        Assert.Throws<ArgumentException>(
            () => SqlFunctionSignatureText.Build(string.Empty, LoansByReader));
    }

    [Fact]
    public void 沒有參數時只有一對空括號()
    {
        Assert.Equal(
            "dbo.fn_Today() RETURNS date",
            SqlFunctionSignatureText.Build(
                "dbo.fn_Today",
                Array.Empty<SqlStatementParameter>(),
                "date").Content);
    }
}
