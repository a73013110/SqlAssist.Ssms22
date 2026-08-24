using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Matching;

namespace SqlAssist.Core;

/// <summary>
/// 依游標處的上下文篩選建議項，並以 <see cref="FuzzyMatcher"/> 的詞首感知分數排名。
/// </summary>
public static class SuggestionMatcher
{
    /// <summary>
    /// 模糊分數的放大倍率。設定成遠大於所有次要調整值的總和，
    /// 確保次要調整只在模糊分數相同時才影響順序。
    /// </summary>
    private const int FuzzyScoreScale = 128;

    /// <summary>完全相同（忽略大小寫）時的壓倒性加成。</summary>
    private const int ExactMatchBonus = 100_000;

    /// <summary>長度懲罰的上限，避免超長物件名稱把分數拉到失真。</summary>
    private const int MaximumLengthPenalty = 64;

    /// <summary>
    /// 篩選並排名建議項，回傳含分數與命中區段的結果。
    /// </summary>
    public static IReadOnlyList<SuggestionMatch> Rank(
        IEnumerable<SqlSuggestion> suggestions,
        SqlCompletionContext context,
        int maximumCount = 100)
    {
        if (suggestions is null)
        {
            throw new ArgumentNullException(nameof(suggestions));
        }

        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (!context.IsValid)
        {
            return Array.Empty<SuggestionMatch>();
        }

        if (maximumCount <= 0)
        {
            return Array.Empty<SuggestionMatch>();
        }

        var pattern = FuzzyMatcher.NormalizePattern(context.Prefix);
        var results = new List<SuggestionMatch>();

        foreach (var suggestion in suggestions)
        {
            if (!IsAllowedForTarget(suggestion.Kind, context.Target) ||
                !IsAllowedForSchema(suggestion, context.SchemaQualifier))
            {
                continue;
            }

            var match = FuzzyMatcher.MatchNormalized(pattern, suggestion.DisplayText);

            if (!match.IsMatch)
            {
                continue;
            }

            results.Add(new SuggestionMatch(suggestion, ComposeScore(suggestion, match, pattern), match.Spans));
        }

        return results
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Suggestion.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Take(maximumCount)
            .ToArray();
    }

    /// <summary>
    /// <see cref="Rank"/> 的簡化版本，只回傳排序後的建議項本身。
    /// </summary>
    public static IReadOnlyList<SqlSuggestion> Match(
        IEnumerable<SqlSuggestion> suggestions,
        SqlCompletionContext context,
        int maximumCount = 100)
    {
        return Rank(suggestions, context, maximumCount)
            .Select(item => item.Suggestion)
            .ToArray();
    }

    /// <summary>
    /// 把模糊分數與次要調整合成最終排名分數。
    /// </summary>
    private static int ComposeScore(SqlSuggestion suggestion, FuzzyMatchResult match, string pattern)
    {
        var score = (match.Score * FuzzyScoreScale) + KindBonus(suggestion.Kind);

        if (pattern.Length > 0 &&
            string.Equals(suggestion.DisplayText, pattern, StringComparison.OrdinalIgnoreCase))
        {
            score += ExactMatchBonus;
        }

        // 分數相同時偏好較短的名稱：使用者通常想要的是最精簡的那個。
        return score - Math.Min(suggestion.DisplayText.Length, MaximumLengthPenalty);
    }

    /// <summary>
    /// 只在模糊分數打平時生效的類別偏好。
    /// </summary>
    private static int KindBonus(SuggestionKind kind)
    {
        return kind switch
        {
            SuggestionKind.Snippet => 40,
            SuggestionKind.Keyword => 30,
            SuggestionKind.Table => 20,
            SuggestionKind.View => 18,
            SuggestionKind.Procedure => 16,
            SuggestionKind.Function => 14,
            SuggestionKind.Schema => 10,
            _ => 0
        };
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
}
