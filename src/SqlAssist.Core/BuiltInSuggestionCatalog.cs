using System.Collections.Generic;

namespace SqlAssist.Core;

public static class BuiltInSuggestionCatalog
{
    public static IReadOnlyList<SqlSuggestion> Create()
    {
        var suggestions = new List<SqlSuggestion>
        {
            new("ssf", "SELECT * FROM ", "SELECT * FROM fragment", "SELECT * FROM", SuggestionKind.Snippet, true),
            new("ap", "ALTER PROCEDURE ", "ALTER PROCEDURE fragment", "ALTER PROCEDURE", SuggestionKind.Snippet, true),
            new("af", "ALTER FUNCTION ", "ALTER FUNCTION fragment", "ALTER FUNCTION", SuggestionKind.Snippet, true)
        };

        foreach (var keyword in SqlKeywordCatalog.SuggestionKeywords)
        {
            suggestions.Add(new SqlSuggestion(
                keyword,
                keyword,
                "T-SQL keyword",
                keyword,
                SuggestionKind.Keyword));
        }

        return suggestions;
    }
}
