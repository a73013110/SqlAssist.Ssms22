using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Core;

public static class SuggestionMatcher
{
    public static IReadOnlyList<SqlSuggestion> Match(
        IEnumerable<SqlSuggestion> suggestions,
        SqlCompletionContext context,
        int maximumCount = 100)
    {
        if (!context.IsValid)
        {
            return Array.Empty<SqlSuggestion>();
        }

        return suggestions
            .Where(item => IsAllowedForTarget(item.Kind, context.Target))
            .Where(item => IsAllowedForSchema(item, context.SchemaQualifier))
            .Select(item => new ScoredSuggestion(item, Score(item, context.Prefix)))
            .Where(item => item.Score < int.MaxValue)
            .OrderBy(item => item.Score)
            .ThenBy(item => item.Suggestion.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Take(maximumCount)
            .Select(item => item.Suggestion)
            .ToArray();
    }

    private static int Score(SqlSuggestion suggestion, string prefix)
    {
        if (prefix.Length == 0)
        {
            return 0;
        }

        var candidate = suggestion.DisplayText;

        if (string.Equals(candidate, prefix, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            if (prefix.Length == 1)
            {
                return KindPriorityForSingleCharacter(suggestion.Kind) + candidate.Length;
            }

            return KindPriority(suggestion.Kind) + candidate.Length - prefix.Length;
        }

        var substringIndex = candidate.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);

        if (substringIndex >= 0)
        {
            return 100 + KindPriority(suggestion.Kind) + substringIndex;
        }

        return IsSubsequence(prefix, candidate)
            ? 200 + KindPriority(suggestion.Kind) + candidate.Length
            : int.MaxValue;
    }

    private static bool IsAllowedForTarget(SuggestionKind kind, CompletionTarget target)
    {
        return target switch
        {
            CompletionTarget.DataSource => kind == SuggestionKind.Table || kind == SuggestionKind.View,
            CompletionTarget.Procedure => kind == SuggestionKind.Procedure,
            CompletionTarget.Function => kind == SuggestionKind.Function,
            _ => true
        };
    }

    private static bool IsAllowedForSchema(SqlSuggestion suggestion, string? schemaQualifier)
    {
        if (string.IsNullOrEmpty(schemaQualifier))
        {
            return true;
        }

        return suggestion.Kind != SuggestionKind.Schema &&
               string.Equals(suggestion.SchemaName, schemaQualifier, StringComparison.OrdinalIgnoreCase);
    }

    private static int KindPriorityForSingleCharacter(SuggestionKind kind)
    {
        return kind switch
        {
            SuggestionKind.Keyword => 0,
            SuggestionKind.Snippet => 50,
            _ => 100
        };
    }

    private static int KindPriority(SuggestionKind kind)
    {
        return kind switch
        {
            SuggestionKind.Snippet => 0,
            SuggestionKind.Keyword => 20,
            _ => 40
        };
    }

    private static bool IsSubsequence(string prefix, string candidate)
    {
        var prefixIndex = 0;

        foreach (var character in candidate)
        {
            if (prefixIndex < prefix.Length &&
                char.ToUpperInvariant(character) == char.ToUpperInvariant(prefix[prefixIndex]))
            {
                prefixIndex++;
            }
        }

        return prefixIndex == prefix.Length;
    }

    private sealed class ScoredSuggestion
    {
        public ScoredSuggestion(SqlSuggestion suggestion, int score)
        {
            Suggestion = suggestion;
            Score = score;
        }

        public SqlSuggestion Suggestion { get; }

        public int Score { get; }
    }
}
