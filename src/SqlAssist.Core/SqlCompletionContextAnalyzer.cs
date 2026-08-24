using System;

namespace SqlAssist.Core;

public static class SqlCompletionContextAnalyzer
{
    public static SqlCompletionContext Analyze(string textBeforeCaret)
    {
        if (textBeforeCaret is null)
        {
            throw new ArgumentNullException(nameof(textBeforeCaret));
        }

        var tokenStart = FindTokenStart(textBeforeCaret);

        if (!SqlLexicalContext.IsCode(textBeforeCaret, tokenStart))
        {
            return new SqlCompletionContext(false, tokenStart, string.Empty, CompletionTarget.Any);
        }

        var prefix = textBeforeCaret.Substring(tokenStart);
        var beforeToken = textBeforeCaret.Substring(0, tokenStart).TrimEnd();
        var schemaQualifier = ExtractSchemaQualifier(beforeToken, out var beforeQualifier);
        var target = DetermineTarget(
            schemaQualifier is null ? beforeToken : beforeQualifier,
            out var targetKeywordStart);
        var isValid = prefix.Length > 0 || target != CompletionTarget.Any || schemaQualifier is not null;
        return new SqlCompletionContext(
            isValid,
            tokenStart,
            prefix,
            target,
            schemaQualifier,
            targetKeywordStart);
    }

    /// <summary>
    /// 依游標前方的關鍵字判斷應該建議哪一類物件，並回報該關鍵字的起點。
    /// </summary>
    private static CompletionTarget DetermineTarget(string text, out int keywordStart)
    {
        if (EndsWithKeywords(text, "ALTER", "PROCEDURE", out keywordStart))
        {
            return CompletionTarget.Procedure;
        }

        if (EndsWithKeywords(text, "ALTER", "FUNCTION", out keywordStart))
        {
            return CompletionTarget.Function;
        }

        if (EndsWithKeyword(text, "FROM", out keywordStart) ||
            EndsWithKeyword(text, "JOIN", out keywordStart) ||
            EndsWithKeyword(text, "UPDATE", out keywordStart) ||
            EndsWithKeyword(text, "INTO", out keywordStart))
        {
            return CompletionTarget.DataSource;
        }

        keywordStart = -1;
        return CompletionTarget.Any;
    }

    private static bool EndsWithKeywords(string text, string first, string second, out int keywordStart)
    {
        keywordStart = -1;
        var secondStart = FindPreviousTokenStart(text, text.Length);
        var secondToken = text.Substring(secondStart);
        var beforeSecond = text.Substring(0, secondStart).TrimEnd();
        var firstStart = FindPreviousTokenStart(beforeSecond, beforeSecond.Length);
        var firstToken = beforeSecond.Substring(firstStart);

        if (!string.Equals(firstToken, first, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(secondToken, second, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        keywordStart = firstStart;
        return true;
    }

    private static bool EndsWithKeyword(string text, string keyword, out int keywordStart)
    {
        var tokenStart = FindPreviousTokenStart(text, text.Length);

        if (!string.Equals(text.Substring(tokenStart), keyword, StringComparison.OrdinalIgnoreCase))
        {
            keywordStart = -1;
            return false;
        }

        keywordStart = tokenStart;
        return true;
    }

    private static string? ExtractSchemaQualifier(string text, out string beforeQualifier)
    {
        beforeQualifier = text;

        if (!text.EndsWith(".", StringComparison.Ordinal))
        {
            return null;
        }

        var beforeDot = text.Substring(0, text.Length - 1).TrimEnd();

        if (beforeDot.EndsWith("]", StringComparison.Ordinal))
        {
            var openingBracket = beforeDot.LastIndexOf('[', beforeDot.Length - 1);

            if (openingBracket >= 0)
            {
                beforeQualifier = beforeDot.Substring(0, openingBracket).TrimEnd();
                return beforeDot
                    .Substring(openingBracket + 1, beforeDot.Length - openingBracket - 2)
                    .Replace("]]", "]");
            }
        }

        var qualifierStart = FindPreviousTokenStart(beforeDot, beforeDot.Length);
        beforeQualifier = beforeDot.Substring(0, qualifierStart).TrimEnd();
        var qualifier = beforeDot.Substring(qualifierStart);
        return qualifier.Length == 0 ? null : qualifier;
    }

    private static int FindTokenStart(string text)
    {
        return FindPreviousTokenStart(text, text.Length);
    }

    private static int FindPreviousTokenStart(string text, int end)
    {
        var index = end;

        while (index > 0 && IsTokenCharacter(text[index - 1]))
        {
            index--;
        }

        return index;
    }

    private static bool IsTokenCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_' || value == '#';
    }
}
