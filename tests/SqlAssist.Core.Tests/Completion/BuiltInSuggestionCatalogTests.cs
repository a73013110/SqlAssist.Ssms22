using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

public sealed class BuiltInSuggestionCatalogTests
{
    /// <summary>右側說明與種類共用 <see cref="SqlKindText"/>，跟著建立當下的介面語言。</summary>
    [Fact]
    public void 關鍵字與系統結構描述的說明跟著介面語言()
    {
        var source = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty);
        SqlSuggestion[] english;
        using (SqlText.Use(SqlLanguage.Find("en")!))
        {
            english = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty).ToArray();
        }

        Assert.Equal("關鍵字", Keyword(source, "SELECT").Description);
        Assert.Equal("結構描述 sys", Schema(source, "sys").Preview);

        Assert.Equal("Keyword", Keyword(english, "SELECT").Description);
        var sys = Schema(english, "sys");
        Assert.Equal("Schema", sys.Description);
        Assert.Equal("Schema sys", sys.Preview);
    }

    private static SqlSuggestion Keyword(IEnumerable<SqlSuggestion> suggestions, string name) =>
        suggestions.Single(suggestion => suggestion.Kind == SuggestionKind.Keyword && suggestion.DisplayText == name);

    private static SqlSuggestion Schema(IEnumerable<SqlSuggestion> suggestions, string name) =>
        suggestions.Single(suggestion => suggestion.Kind == SuggestionKind.Schema && suggestion.DisplayText == name);
}
