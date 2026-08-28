using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Matching;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 依游標處的上下文篩選建議項，並以 <see cref="FuzzyMatcher"/> 的詞首感知分數排名。
/// </summary>
public static class SuggestionMatcher
{
    /// <summary>
    /// 分數的四個層級。
    /// </summary>
    /// <remarks>
    /// 每一層的倍率都大於它底下所有層的最大總和，因此低層只在高層打平時才說得上話：
    ///
    ///   比對品質（8192／分） ＞ 最近用過（3072） ＞ 類別（最多 40×64＝2560） ＞ 名稱長度（最多 63）
    ///
    /// 這個關係必須維持。倍率一旦太靠近，低層就會翻過高層——曾經
    /// 長度懲罰上限 64、類別加成最多 40，兩者同一量級，於是
    /// <c>USER_ACCOUNT_HISTORY_DETAIL</c>（欄位 35−27＝8）輸給
    /// <c>USERS</c>（資料表 20−5＝15），正好違反「欄位優先於資料表」。
    /// </remarks>
    private const int FuzzyScoreScale = 8192;

    /// <summary>最近提交過的加成；壓得過類別偏好，壓不過更好的比對品質。</summary>
    private const int RecentlyUsedBonus = 3072;

    /// <summary>類別偏好的倍率。</summary>
    private const int KindBonusScale = 64;

    /// <summary>長度懲罰的上限，避免超長物件名稱把分數拉到失真。</summary>
    private const int MaximumLengthPenalty = 63;

    /// <summary>完全相同（忽略大小寫）時的壓倒性加成。</summary>
    private const int ExactMatchBonus = 10_000_000;

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
                !IsAllowedForPosition(suggestion, context) ||
                !IsAllowedForSchema(suggestion, context))
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

        // 同分時保留候選清單原本的順序（LINQ 的排序是穩定的），不再改成字母序：
        // 每一段的原始順序本身就有意義——欄位是資料表的定義順序，
        // 資料庫物件與關鍵字是名稱順序。字母序會把欄位的定義順序打散，
        // 而那才是使用者對一張表的心智模型。
        return results
            .OrderByDescending(item => item.Score)
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
    /// 只做上下文過濾，不做前綴比對與排名。
    /// </summary>
    /// <remarks>
    /// 原生引擎把清單交給平台快取，之後每一次按鍵只重新比對前綴，
    /// 因此上下文過濾必須在建立清單時就做完。
    /// </remarks>
    public static IReadOnlyList<SqlSuggestion> Filter(
        IEnumerable<SqlSuggestion> suggestions,
        SqlCompletionContext context)
    {
        if (suggestions is null)
        {
            throw new ArgumentNullException(nameof(suggestions));
        }

        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var results = new List<SqlSuggestion>();

        foreach (var suggestion in suggestions)
        {
            if (IsAllowedForTarget(suggestion.Kind, context.Target) &&
                IsAllowedForPosition(suggestion, context) &&
                IsAllowedForSchema(suggestion, context))
            {
                results.Add(suggestion);
            }
        }

        return results;
    }

    /// <summary>
    /// 把模糊分數與次要調整合成最終排名分數。
    /// </summary>
    public static int ComposeScore(SqlSuggestion suggestion, FuzzyMatchResult match, string pattern)
    {
        var score = (match.Score * FuzzyScoreScale) + ComposeStandingScore(suggestion);

        if (pattern.Length > 0 &&
            string.Equals(suggestion.DisplayText, pattern, StringComparison.OrdinalIgnoreCase))
        {
            score += ExactMatchBonus;
        }

        // 分數相同時偏好較短的名稱：使用者通常想要的是最精簡的那個。
        return score - Math.Min(suggestion.DisplayText.Length, MaximumLengthPenalty);
    }

    /// <summary>
    /// 與使用者輸入無關的那一段分數：最近用過與類別偏好。
    /// </summary>
    /// <remarks>
    /// 還沒輸入任何字元時，這就是清單的順序——比對品質這一層此時對所有候選項
    /// 都是零，剩下的正好是「在不知道他要打什麼的情況下，最可能要的東西」。
    /// 與 <see cref="ComposeScore"/> 共用同一組層級，兩種情境的偏好才會一致。
    /// </remarks>
    public static int ComposeStandingScore(SqlSuggestion suggestion)
    {
        var score = KindBonus(suggestion.Kind) * KindBonusScale;

        if (SqlSuggestionUsage.IsRecent(suggestion))
        {
            score += RecentlyUsedBonus;
        }

        return score;
    }

    /// <summary>
    /// 類別偏好；由 <see cref="KindBonusScale"/> 放大成一個層級，
    /// 只在比對品質與最近使用都打平時決定順序。
    /// </summary>
    private static int KindBonus(SuggestionKind kind)
    {
        return kind switch
        {
            SuggestionKind.Snippet => 40,

            // 欄位只會在敘述真的看得到它們時才進入候選，因此排在資料表之上：
            // 在 SELECT 或 WHERE 位置輸入前綴時，要的幾乎都是欄位。
            SuggestionKind.Column => 35,
            SuggestionKind.Keyword => 30,

            // 內建函式排在關鍵字之下：分數打平時，使用者要的比較可能是
            // 文法上非有不可的那個字。
            SuggestionKind.BuiltInFunction => 28,

            // 只有 USE 之後才會出現，那個位置沒有別的東西跟它競爭。
            SuggestionKind.Database => 25,
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
            CompletionTarget.Column => kind == SuggestionKind.Column,
            CompletionTarget.Database => kind == SuggestionKind.Database,

            // 沒有限定字時仍然可以有欄位：SELECT | FROM PUBLISHER a 這種位置，
            // 敘述裡看得到的欄位比整個資料庫的物件清單更接近使用者要的東西。
            // 候選清單是依上下文組出來的，沒有範圍就不會有欄位，這裡不必再擋。
            _ => true
        };
    }

    /// <summary>
    /// 關鍵字與內建函式要落在文法允許它出現的位置。
    /// </summary>
    /// <remarks>
    /// 只對這兩種生效。資料庫物件與 Snippet 的位置一律是
    /// <see cref="SqlKeywordPosition.Any"/>，交集永遠不為空，
    /// 因此不必特別放行；但明寫出來比依賴預設值可靠。
    ///
    /// 內建函式一起收在這裡的理由與關鍵字相同：語句開頭、資料來源位置與
    /// DDL 物件位置不該冒出 <c>COUNT</c>。
    /// </remarks>
    private static bool IsAllowedForPosition(SqlSuggestion suggestion, SqlCompletionContext context)
    {
        if (suggestion.Kind is not (SuggestionKind.Keyword or SuggestionKind.BuiltInFunction))
        {
            return true;
        }

        return (suggestion.Positions & context.KeywordPosition) != SqlKeywordPosition.None;
    }

    /// <summary>
    /// 限定字要當成結構描述來過濾。
    /// </summary>
    /// <remarks>
    /// 限定字已經解析成資料來源時（<c>u.</c>），建議清單裡放的是欄位，
    /// 欄位沒有結構描述可比，這時不再套用結構描述過濾。
    /// </remarks>
    private static bool IsAllowedForSchema(SqlSuggestion suggestion, SqlCompletionContext context)
    {
        if (context.Target == CompletionTarget.Column || string.IsNullOrEmpty(context.Qualifier))
        {
            return true;
        }

        return suggestion.Kind != SuggestionKind.Schema &&
               string.Equals(suggestion.SchemaName, context.Qualifier, StringComparison.OrdinalIgnoreCase);
    }
}
