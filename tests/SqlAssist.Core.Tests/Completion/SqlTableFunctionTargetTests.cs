using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Snippets;
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

    private static readonly IReadOnlyList<SqlSuggestion> BuiltIn = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty);

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

        return SuggestionContextFilter.Filter(Candidates, context)
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

    /// <summary>
    /// 左邊來源的資料行也是 APPLY 右邊的開頭：<c>CROSS APPLY Doc.nodes('…')</c> 對 XML 資料行呼叫方法，
    /// 衍生資料表的資料行清單取的名稱也算。
    /// </summary>
    [Fact]
    public void APPLY之後列得出左邊來源的資料行()
    {
        var sql = "SELECT * FROM (SELECT dbo.fn_LoanXml()) AS d(Doc) CROSS APPLY ";
        var context = SqlCompletionContextAnalyzer.Analyze(sql, sql.Length);
        var column = new SqlSuggestion("Doc", "Doc", string.Empty, string.Empty, SuggestionKind.Column);

        Assert.Equal(CompletionTarget.TableFunction, context.Target);
        Assert.Contains(context.ScopeSources, source => source.Names.Contains("Doc"));
        Assert.Single(SuggestionContextFilter.Filter(new[] { column }, context));
    }

    /// <remarks>
    /// 資料列集函式不在中繼資料裡：關鍵字的 <c>OPENROWSET</c>、<c>OPENXML</c> 與函式目錄的 <c>OPENJSON</c>
    /// 由位置旗標認出來。曾經整份被目標擋掉，<c>FROM </c> 之後一個都列不出來。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM ")]
    [InlineData("SELECT * FROM dbo.Loan l CROSS APPLY ")]
    public void 資料來源與APPLY列得出資料列集函式(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var names = SuggestionContextFilter.Filter(BuiltIn, context).Select(item => item.DisplayText).ToArray();

        Assert.Contains("OPENROWSET", names);
        Assert.Contains("OPENXML", names);
        Assert.Contains("OPENJSON", names);

        // 其餘的關鍵字與純量內建函式照樣擋掉。
        Assert.DoesNotContain("SELECT", names);
        Assert.DoesNotContain("COUNT", names);
    }

    [Theory]
    [InlineData("SELECT ")]
    [InlineData("SELECT * FROM dbo.")]
    public void 資料列集函式不在運算式與限定字之後(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var names = SuggestionContextFilter.Filter(BuiltIn, context).Select(item => item.DisplayText).ToArray();

        Assert.DoesNotContain("OPENJSON", names);
        Assert.DoesNotContain("OPENROWSET", names);
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
