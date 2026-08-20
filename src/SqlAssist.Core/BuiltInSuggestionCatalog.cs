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

        foreach (var keyword in Keywords)
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

    private static readonly string[] Keywords =
    {
        "ALTER", "AND", "AS", "BEGIN", "BY", "CASE", "CREATE", "CROSS",
        "DECLARE", "DELETE", "DISTINCT", "DROP", "ELSE", "END", "EXEC",
        "EXECUTE", "EXISTS", "FROM", "FULL", "FUNCTION", "GROUP", "HAVING",
        "IF", "IN", "INNER", "INSERT", "INTO", "JOIN", "LEFT", "MERGE",
        "NOT", "NULL", "ON", "OR", "ORDER", "OUTER", "PROCEDURE", "RETURN",
        "RIGHT", "SELECT", "SET", "TABLE", "THEN", "TOP", "UNION", "UPDATE",
        "VALUES", "VIEW", "WHEN", "WHERE", "WITH"
    };
}

