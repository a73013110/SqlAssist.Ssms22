using System;
using System.Collections.Generic;
using SqlAssist.Core.Statements;
using Xunit;

namespace SqlAssist.Core.Tests.Statements;

public sealed class SqlFunctionCallTextTests
{
    private static readonly SqlStatementParameter[] LoansByReader =
    {
        new("@readerId", "int", isOutput: false, isOptional: false),
        new("@since", "datetime2(7)", isOutput: false, isOptional: false),
        new("@branch", "nvarchar(40)", isOutput: false, isOptional: true)
    };

    private static string Build(IReadOnlyList<SqlStatementParameter> parameters, out int caretOffset) =>
        SqlFunctionCallText.Build("[dbo].[fn_LoansByReader]", parameters, out caretOffset);

    /// <remarks>
    /// 一行、只有值：函式只收位置引數，具名傳值在文法上就不成立，
    /// 而它出現在運算式中間，拆成多列會把使用者正在寫的那句話撐開。
    /// </remarks>
    [Fact]
    public void 依型別填入預留值並排成一行()
    {
        var text = Build(LoansByReader, out _);

        Assert.Equal("[dbo].[fn_LoansByReader](0, NULL, N'')", text);
    }

    /// <remarks>
    /// 定義裡寫了預設值的參數<b>照樣</b>要佔一個位置：函式沒有「省略」這回事，
    /// 省略的寫法是 DEFAULT 這個關鍵字。與 EXEC 骨架把選擇性參數標出來相反。
    /// </remarks>
    [Fact]
    public void 有預設值的參數也要佔一個位置()
    {
        Assert.Equal(3, Build(LoansByReader, out _).Split(',').Length);
    }

    /// <remarks>
    /// <c>SELECT dbo.fn_Today</c> 是語法錯誤，<c>SELECT dbo.fn_Today()</c> 才不是；
    /// 那對括號正是使用者少按的兩次鍵。
    /// </remarks>
    [Fact]
    public void 沒有參數時補一對空括號()
    {
        var text = SqlFunctionCallText.Build(
            "[dbo].[fn_Today]",
            Array.Empty<SqlStatementParameter>(),
            out var caretOffset);

        Assert.Equal("[dbo].[fn_Today]()", text);
        Assert.Equal(text.Length - 1, caretOffset);
    }

    /// <remarks>游標停在第一個引數上：那是使用者接下來要改的第一個東西。</remarks>
    [Fact]
    public void 游標停在第一個引數()
    {
        var text = Build(LoansByReader, out var caretOffset);

        Assert.Equal("[dbo].[fn_LoansByReader](".Length, caretOffset);
        Assert.Equal("0, NULL, N'')", text.Substring(caretOffset));
    }

    [Fact]
    public void 名稱為空時丟出例外()
    {
        Assert.Throws<ArgumentException>(
            () => SqlFunctionCallText.Build(string.Empty, LoansByReader, out _));
    }
}
