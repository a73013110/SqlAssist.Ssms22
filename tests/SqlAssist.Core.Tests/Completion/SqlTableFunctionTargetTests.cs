using System.Linq;
using SqlAssist.Core.Completion;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 資料表值函式的位置。
/// </summary>
/// <remarks>
/// 它同時是資料來源（<c>FROM dbo.fn_LoansByReader(1)</c>）與可以 <c>ALTER</c>／
/// <c>DROP</c> 的函式，因此不能併進資料表也不能併進純量函式——
/// 併進前者的話 <c>DROP FUNCTION</c> 列不出它，併進後者的話 <c>FROM</c> 列不出它，
/// 而後面這一種正是它一開始從 <c>FROM</c> 清單裡消失的原因。
/// </remarks>
public sealed class SqlTableFunctionTargetTests
{
    private static SqlSuggestion Suggestion(SuggestionKind kind, string name) =>
        new(name, name, string.Empty, string.Empty, kind, schemaName: "dbo");

    private static readonly SqlSuggestion[] Candidates =
    {
        Suggestion(SuggestionKind.Table, "Lib_Reader"),
        Suggestion(SuggestionKind.View, "vw_LoanSummary"),
        Suggestion(SuggestionKind.TableFunction, "fn_LoansByReader"),
        Suggestion(SuggestionKind.Function, "fn_DueDate"),
        Suggestion(SuggestionKind.Procedure, "usp_Loan_Renew")
    };

    private static string[] Filter(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        return SuggestionMatcher.Filter(Candidates, context)
            .Select(item => item.DisplayText)
            .ToArray();
    }

    [Theory]
    [InlineData("SELECT * FROM ")]
    [InlineData("SELECT * FROM dbo.Loan l INNER JOIN ")]
    public void 資料來源位置列得出資料表值函式(string textBeforeCaret)
    {
        var names = Filter(textBeforeCaret);

        Assert.Contains("fn_LoansByReader", names);
        Assert.Contains("Lib_Reader", names);

        // 純量函式回傳的是一個值，放在 FROM 後面剖析不過。
        Assert.DoesNotContain("fn_DueDate", names);
    }

    /// <remarks>
    /// <c>APPLY</c> 之後文法上只接得了資料表值函式與衍生資料表。曾經把它歸在
    /// <see cref="CompletionTarget.Function"/>，代價是清單裡混進一批選不中的純量函式；
    /// 現在有了自己的目標，順帶讓提交時分得出「要補引數」還是「只要名稱」。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM dbo.Loan l CROSS APPLY ")]
    [InlineData("SELECT * FROM dbo.Loan l OUTER APPLY ")]
    public void APPLY之後只列資料表值函式(string textBeforeCaret)
    {
        Assert.Equal(
            CompletionTarget.TableFunction,
            SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).Target);

        Assert.Equal(new[] { "fn_LoansByReader" }, Filter(textBeforeCaret));
    }

    /// <remarks>兩種函式都改得動也刪得掉，DDL 位置不能少列一種。</remarks>
    [Theory]
    [InlineData("ALTER FUNCTION ")]
    [InlineData("DROP FUNCTION ")]
    public void 函式的DDL位置兩種都列(string textBeforeCaret)
    {
        var names = Filter(textBeforeCaret);

        Assert.Equal(new[] { "fn_LoansByReader", "fn_DueDate" }, names);
    }
}
