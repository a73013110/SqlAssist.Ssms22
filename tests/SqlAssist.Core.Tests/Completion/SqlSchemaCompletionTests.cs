using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 結構描述在哪些位置列得出來。
/// </summary>
/// <remarks>
/// 結構描述是多段式名稱的一段，與資料庫、連結伺服器同一條規則：凡是寫得出名稱開頭的
/// 位置就該有它。曾經只有運算式位置列得出來，<c>FROM</c>、<c>EXEC</c>、<c>APPLY</c>
/// 之後沒有，於是 <c>INFORMATION_SCHEMA</c> 這種長名稱只能整串背下來自己打。
/// </remarks>
public sealed class SqlSchemaCompletionTests
{
    private static readonly SqlSuggestion UserSchema =
        new("dbo", "dbo", "Schema", "Schema dbo", SuggestionKind.Schema, schemaName: "dbo");

    private static readonly SqlSuggestion Table =
        new("Loan", "Loan", "Table", "Loan", SuggestionKind.Table, schemaName: "dbo");

    private static SqlCompletionContext Analyze(string sqlWithCaret, SqlQualifierSlot? leftmost = null)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        if (leftmost is { } slot && context.QualifierPath!.TryRealign(slot, out var realigned))
        {
            context = context.WithQualifierPath(realigned);
        }

        return context;
    }

    /// <summary>內建的兩個系統結構描述，加上資料庫裡來的一個結構描述與一張表。</summary>
    private static SqlSuggestion[] Candidates() =>
        BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty)
            .Append(UserSchema)
            .Append(Table)
            .ToArray();

    private static string[] Schemas(string sqlWithCaret, SqlQualifierSlot? leftmost = null)
    {
        return SuggestionMatcher
            .Filter(Candidates(), Analyze(sqlWithCaret, leftmost))
            .Where(suggestion => suggestion.Kind == SuggestionKind.Schema)
            .Select(suggestion => suggestion.DisplayText)
            .ToArray();
    }

    [Theory]
    [InlineData("SELECT * FROM |")]
    [InlineData("SELECT * FROM Loan l JOIN |")]
    [InlineData("EXEC |")]
    [InlineData("SELECT * FROM Loan l CROSS APPLY |")]
    [InlineData("SELECT |")]
    public void 名稱開頭的位置列得出使用者與系統結構描述(string sqlWithCaret)
    {
        var names = Schemas(sqlWithCaret);

        Assert.Contains("dbo", names);
        Assert.Contains("sys", names);
        Assert.Contains("INFORMATION_SCHEMA", names);
    }

    /// <remarks>
    /// 排名不為結構描述開特例：打出前綴時由模糊比對主導，長名稱自然排上來。
    /// </remarks>
    [Fact]
    public void 打出前綴就排到INFORMATION_SCHEMA()
    {
        var ranked = SuggestionMatcher.Match(Candidates(), Analyze("SELECT * FROM info|"));

        Assert.Equal("INFORMATION_SCHEMA", ranked.First().DisplayText);
    }

    /// <remarks>
    /// 系統物件改不動也刪不掉，列出那兩個結構描述只會讓使用者往一條走不通的路打下去。
    /// </remarks>
    [Theory]
    [InlineData("ALTER FUNCTION |")]
    [InlineData("DROP VIEW |")]
    [InlineData("ALTER PROCEDURE |")]
    public void 修改位置只列使用者結構描述(string sqlWithCaret)
    {
        Assert.Equal(new[] { "dbo" }, Schemas(sqlWithCaret));
    }

    [Fact]
    public void USE之後不列結構描述()
    {
        Assert.Empty(Schemas("USE |"));
    }

    [Fact]
    public void 結構描述之後不列結構描述()
    {
        Assert.Empty(Schemas("SELECT * FROM dbo.|"));
    }

    [Fact]
    public void 連結伺服器之後不列結構描述()
    {
        Assert.Empty(Schemas("SELECT * FROM LibMirror.|", SqlQualifierSlot.Server));
    }

    /// <remarks>
    /// 資料庫之後的下一段就是結構描述。清單來自那個資料庫的目錄，這裡只守
    /// 「這一格放得進結構描述」。
    /// </remarks>
    [Fact]
    public void 資料庫之後列得出結構描述()
    {
        Assert.Contains("dbo", Schemas("SELECT * FROM LibArchive.|", SqlQualifierSlot.Database));
    }

    [Theory]
    [InlineData("SELECT * FROM ", true)]
    [InlineData("SELECT ", true)]
    [InlineData("SELECT * FROM Loan l OUTER APPLY ", true)]
    [InlineData("EXEC ", true)]
    [InlineData("ALTER PROCEDURE ", false)]
    [InlineData("DROP PROCEDURE ", false)]
    [InlineData("ALTER FUNCTION ", false)]
    [InlineData("DROP VIEW ", false)]
    [InlineData("SELECT NEXT VALUE FOR ", false)]
    [InlineData("USE ", false)]
    public void 系統結構描述只出現在接得到系統物件的位置(string textBeforeCaret, bool expected)
    {
        Assert.Equal(expected, SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).WantsSystemSchemas);
    }
}
