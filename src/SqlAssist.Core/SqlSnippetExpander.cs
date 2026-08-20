using System;
using System.Collections.Generic;

namespace SqlAssist.Core;

public sealed class SqlSnippetExpander
{
    private static readonly IReadOnlyDictionary<string, string> Snippets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ssf"] = "SELECT * FROM ",
            ["ap"] = "ALTER PROCEDURE ",
            ["af"] = "ALTER FUNCTION "
        };

    private static readonly ISet<string> Keywords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "alter", "and", "as", "begin", "by", "case", "create", "cross",
            "declare", "delete", "distinct", "drop", "else", "end", "exec",
            "execute", "exists", "from", "full", "function", "group", "having",
            "if", "in", "inner", "insert", "into", "join", "left", "merge",
            "not", "null", "on", "or", "order", "outer", "procedure", "return",
            "right", "select", "set", "table", "then", "top", "union", "update",
            "values", "view", "when", "where", "with"
        };

    public bool TryExpand(string textBeforeCaret, out ExpansionResult? result)
    {
        if (string.IsNullOrEmpty(textBeforeCaret))
        {
            result = null;
            return false;
        }

        var tokenStart = FindTokenStart(textBeforeCaret);
        if (tokenStart == textBeforeCaret.Length || !SqlLexicalContext.IsCode(textBeforeCaret, tokenStart))
        {
            result = null;
            return false;
        }

        var token = textBeforeCaret.Substring(tokenStart);

        if (Snippets.TryGetValue(token, out var snippet))
        {
            result = new ExpansionResult(tokenStart, token.Length, snippet, ExpansionKind.Snippet);
            return true;
        }

        if (Keywords.Contains(token) && !string.Equals(token, token.ToUpperInvariant(), StringComparison.Ordinal))
        {
            result = new ExpansionResult(tokenStart, token.Length, token.ToUpperInvariant(), ExpansionKind.Keyword);
            return true;
        }

        result = null;
        return false;
    }

    private static int FindTokenStart(string text)
    {
        var index = text.Length;

        while (index > 0 && IsTokenCharacter(text[index - 1]))
        {
            index--;
        }

        return index;
    }

    private static bool IsTokenCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }
}

