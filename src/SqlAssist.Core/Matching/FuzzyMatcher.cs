using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Matching;

/// <summary>
/// 詞首感知的模糊比對器，演算法沿用 fzf v2 的 Smith-Waterman 變體：
/// 以動態規劃找出總分最高的對齊方式，再回溯取得實際命中的字元位置。
/// </summary>
/// <remarks>
/// 相對於原始 fzf，這裡針對 SQL 識別字調整字元分類：底線、井號、小老鼠與點號
/// 都歸類為分隔符，因此 <c>libr</c> 比對 <c>Lib_Reader</c> 時，<c>S</c> 與 <c>U</c>
/// 兩處都會取得詞首加成，分數會高於單純把 <c>libr</c> 當子字串命中的候選項。
/// </remarks>
public static class FuzzyMatcher
{
    /// <summary>每命中一個字元的基本分數。</summary>
    public const int ScoreMatch = 16;

    /// <summary>跳過字元時，第一個被跳過字元的懲罰。</summary>
    public const int ScoreGapStart = -3;

    /// <summary>跳過字元時，後續每個被跳過字元的懲罰。</summary>
    public const int ScoreGapExtension = -1;

    /// <summary>命中詞首（前一個字元是非文字字元）的加成。</summary>
    public const int BonusBoundary = ScoreMatch / 2;

    /// <summary>命中空白之後第一個字元的加成。</summary>
    public const int BonusBoundaryWhite = BonusBoundary + 2;

    /// <summary>命中分隔符（SQL 中主要是底線）之後第一個字元的加成。</summary>
    public const int BonusBoundaryDelimiter = BonusBoundary + 1;

    /// <summary>命中非文字字元本身的加成。</summary>
    public const int BonusNonWord = ScoreMatch / 2;

    /// <summary>命中 camelCase 轉折或字母後第一個數字的加成。</summary>
    public const int BonusCamel = BonusBoundary + ScoreGapExtension;

    /// <summary>連續命中的加成，設計上剛好抵銷一次 gap 的代價。</summary>
    public const int BonusConsecutive = -(ScoreGapStart + ScoreGapExtension);

    /// <summary>命中候選字串第一個字元時，詞首加成的倍率。</summary>
    public const int BonusFirstCharMultiplier = 2;

    /// <summary>SQL 識別字中常見、應視為詞界的分隔符。</summary>
    private const string SqlDelimiters = "_.#@$-/\\:,;|";

    private static readonly int[] EmptyPositions = Array.Empty<int>();

    /// <summary>
    /// 將使用者輸入正規化成可重複使用的比對樣式。熱路徑上請先呼叫一次，
    /// 再以 <see cref="MatchNormalized"/> 比對大量候選項。
    /// </summary>
    public static string NormalizePattern(string pattern)
    {
        if (pattern is null)
        {
            throw new ArgumentNullException(nameof(pattern));
        }

        return pattern.ToLowerInvariant();
    }

    /// <summary>比對單一候選項；樣式會在內部正規化。</summary>
    public static FuzzyMatchResult Match(string pattern, string candidate)
    {
        if (pattern is null)
        {
            throw new ArgumentNullException(nameof(pattern));
        }

        return MatchNormalized(NormalizePattern(pattern), candidate);
    }

    /// <summary>
    /// 以已正規化（全小寫）的樣式比對候選項。空樣式一律視為命中且分數為零，
    /// 讓呼叫端可以在沒有輸入時列出全部候選項。
    /// </summary>
    public static FuzzyMatchResult MatchNormalized(string normalizedPattern, string candidate)
    {
        if (normalizedPattern is null)
        {
            throw new ArgumentNullException(nameof(normalizedPattern));
        }

        if (candidate is null)
        {
            throw new ArgumentNullException(nameof(candidate));
        }

        if (normalizedPattern.Length == 0)
        {
            return FuzzyMatchResult.Matched(0, Array.Empty<MatchSpan>());
        }

        if (candidate.Length == 0 || normalizedPattern.Length > candidate.Length)
        {
            return FuzzyMatchResult.NoMatch;
        }

        // 先做免配置的子序列預檢；建議清單中絕大多數候選項會在這裡就被淘汰。
        if (!IsSubsequence(normalizedPattern, candidate))
        {
            return FuzzyMatchResult.NoMatch;
        }

        return normalizedPattern.Length == 1
            ? MatchSingleCharacter(normalizedPattern[0], candidate)
            : MatchCore(normalizedPattern, candidate);
    }

    /// <summary>
    /// 便宜的子序列檢查，用來在配置 DP 陣列之前淘汰不可能命中的候選項。
    /// </summary>
    public static bool IsSubsequence(string normalizedPattern, string candidate)
    {
        if (normalizedPattern is null)
        {
            throw new ArgumentNullException(nameof(normalizedPattern));
        }

        if (candidate is null)
        {
            throw new ArgumentNullException(nameof(candidate));
        }

        var patternIndex = 0;

        for (var index = 0; index < candidate.Length && patternIndex < normalizedPattern.Length; index++)
        {
            if (char.ToLowerInvariant(candidate[index]) == normalizedPattern[patternIndex])
            {
                patternIndex++;
            }
        }

        return patternIndex == normalizedPattern.Length;
    }

    /// <summary>
    /// 單字元樣式的快速路徑。觸發字元數預設為 1，這是最熱的情境，
    /// 因此完全不配置 DP 陣列。
    /// </summary>
    private static FuzzyMatchResult MatchSingleCharacter(char patternChar, string candidate)
    {
        var previousClass = CharacterClass.White;
        var bestScore = int.MinValue;
        var bestPosition = -1;

        for (var index = 0; index < candidate.Length; index++)
        {
            var currentClass = ClassOf(candidate[index]);
            var bonus = BonusFor(previousClass, currentClass);
            previousClass = currentClass;

            if (char.ToLowerInvariant(candidate[index]) != patternChar)
            {
                continue;
            }

            var score = ScoreMatch + (bonus * BonusFirstCharMultiplier);

            if (score > bestScore)
            {
                bestScore = score;
                bestPosition = index;
            }

            // 已經命中詞首，後面不可能出現更好的位置。
            if (bonus >= BonusBoundary)
            {
                break;
            }
        }

        return bestPosition < 0
            ? FuzzyMatchResult.NoMatch
            : FuzzyMatchResult.Matched(bestScore, new[] { new MatchSpan(bestPosition, 1) });
    }

    private static FuzzyMatchResult MatchCore(string pattern, string candidate)
    {
        var patternLength = pattern.Length;
        var candidateLength = candidate.Length;

        var bonuses = new int[candidateLength];
        var firstRowScores = new int[candidateLength];
        var firstRowConsecutive = new int[candidateLength];
        var firstOccurrences = new int[patternLength];

        // 第一輪：計算每個位置的詞界加成，同時取得每個樣式字元最左的可行位置。
        var previousClass = CharacterClass.White;
        var patternIndex = 0;
        var lastIndex = 0;
        var firstPatternChar = pattern[0];
        var currentPatternChar = pattern[0];
        var previousScore = 0;
        var inGap = false;

        for (var index = 0; index < candidateLength; index++)
        {
            var currentClass = ClassOf(candidate[index]);
            var lowered = char.ToLowerInvariant(candidate[index]);
            var bonus = BonusFor(previousClass, currentClass);
            bonuses[index] = bonus;
            previousClass = currentClass;

            if (lowered == currentPatternChar)
            {
                if (patternIndex < patternLength)
                {
                    firstOccurrences[patternIndex] = index;
                    patternIndex++;
                    currentPatternChar = pattern[Math.Min(patternIndex, patternLength - 1)];
                }

                lastIndex = index;
            }

            if (lowered == firstPatternChar)
            {
                firstRowScores[index] = ScoreMatch + (bonus * BonusFirstCharMultiplier);
                firstRowConsecutive[index] = 1;
                inGap = false;
            }
            else
            {
                firstRowScores[index] = Math.Max(
                    previousScore + (inGap ? ScoreGapExtension : ScoreGapStart),
                    0);
                firstRowConsecutive[index] = 0;
                inGap = true;
            }

            previousScore = firstRowScores[index];
        }

        if (patternIndex != patternLength)
        {
            return FuzzyMatchResult.NoMatch;
        }

        // 第二輪：只在 [第一個字元最左位置, 最後一個字元最右位置] 這段區間內做 DP。
        var origin = firstOccurrences[0];
        var width = lastIndex - origin + 1;
        var scores = new int[patternLength * width];
        var consecutives = new int[patternLength * width];
        Array.Copy(firstRowScores, origin, scores, 0, width);
        Array.Copy(firstRowConsecutive, origin, consecutives, 0, width);

        var bestScore = 0;
        var bestPosition = origin;

        for (var row = 1; row < patternLength; row++)
        {
            var rowOffset = row * width;
            var previousRowOffset = rowOffset - width;
            var rowPatternChar = pattern[row];
            var rowGap = false;

            for (var index = firstOccurrences[row]; index <= lastIndex; index++)
            {
                var column = index - origin;
                var matchScore = 0;
                var consecutive = 0;
                var skipScore = scores[rowOffset + column - 1] +
                                (rowGap ? ScoreGapExtension : ScoreGapStart);

                if (char.ToLowerInvariant(candidate[index]) == rowPatternChar)
                {
                    matchScore = scores[previousRowOffset + column - 1] + ScoreMatch;
                    var bonus = bonuses[index];
                    consecutive = consecutives[previousRowOffset + column - 1] + 1;

                    if (consecutive > 1)
                    {
                        var runStartBonus = bonuses[index - consecutive + 1];

                        if (bonus >= BonusBoundary && bonus > runStartBonus)
                        {
                            // 這個位置本身就是更強的詞首，從這裡重新起算連續命中。
                            consecutive = 1;
                        }
                        else
                        {
                            bonus = Math.Max(bonus, Math.Max(BonusConsecutive, runStartBonus));
                        }
                    }

                    if (matchScore + bonus < skipScore)
                    {
                        matchScore += bonuses[index];
                        consecutive = 0;
                    }
                    else
                    {
                        matchScore += bonus;
                    }
                }

                consecutives[rowOffset + column] = consecutive;
                rowGap = matchScore < skipScore;
                var score = Math.Max(Math.Max(matchScore, skipScore), 0);

                if (row == patternLength - 1 && score > bestScore)
                {
                    bestScore = score;
                    bestPosition = index;
                }

                scores[rowOffset + column] = score;
            }
        }

        var positions = Backtrack(
            scores,
            consecutives,
            firstOccurrences,
            width,
            origin,
            patternLength,
            bestPosition);

        return FuzzyMatchResult.Matched(bestScore, ToSpans(positions));
    }

    /// <summary>
    /// 從分數矩陣回溯出實際命中的字元位置。若回溯結果與樣式長度不符
    /// （理論上不會發生），改用貪婪的左優先對齊，確保永遠能給出可用的高亮。
    /// </summary>
    private static int[] Backtrack(
        int[] scores,
        int[] consecutives,
        int[] firstOccurrences,
        int width,
        int origin,
        int patternLength,
        int bestPosition)
    {
        var positions = new List<int>(patternLength);
        var row = patternLength - 1;
        var index = bestPosition;
        var preferMatch = true;

        while (index >= origin)
        {
            var rowOffset = row * width;
            var column = index - origin;
            var current = scores[rowOffset + column];
            var diagonal = 0;
            var left = 0;

            if (row > 0 && index >= firstOccurrences[row])
            {
                diagonal = scores[rowOffset - width + column - 1];
            }

            if (index > firstOccurrences[row])
            {
                left = scores[rowOffset + column - 1];
            }

            if (current > diagonal && (current > left || (current == left && preferMatch)))
            {
                positions.Add(index);

                if (row == 0)
                {
                    break;
                }

                row--;
            }

            var lookahead = rowOffset + width + column + 1;
            preferMatch = consecutives[rowOffset + column] > 1 ||
                          (lookahead < consecutives.Length && consecutives[lookahead] > 0);
            index--;
        }

        if (positions.Count != patternLength)
        {
            return EmptyPositions;
        }

        positions.Reverse();
        return positions.ToArray();
    }

    private static MatchSpan[] ToSpans(int[] positions)
    {
        if (positions.Length == 0)
        {
            return Array.Empty<MatchSpan>();
        }

        var spans = new List<MatchSpan>(positions.Length);
        var start = positions[0];
        var length = 1;

        for (var index = 1; index < positions.Length; index++)
        {
            if (positions[index] == positions[index - 1] + 1)
            {
                length++;
                continue;
            }

            spans.Add(new MatchSpan(start, length));
            start = positions[index];
            length = 1;
        }

        spans.Add(new MatchSpan(start, length));
        return spans.ToArray();
    }

    private static int BonusFor(CharacterClass previous, CharacterClass current)
    {
        if (current > CharacterClass.NonWord)
        {
            switch (previous)
            {
                case CharacterClass.White:
                    return BonusBoundaryWhite;
                case CharacterClass.Delimiter:
                    return BonusBoundaryDelimiter;
                case CharacterClass.NonWord:
                    return BonusBoundary;
            }
        }

        if ((previous == CharacterClass.Lower && current == CharacterClass.Upper) ||
            (previous != CharacterClass.Digit && current == CharacterClass.Digit))
        {
            return BonusCamel;
        }

        switch (current)
        {
            case CharacterClass.NonWord:
            case CharacterClass.Delimiter:
                return BonusNonWord;
            case CharacterClass.White:
                return BonusBoundaryWhite;
            default:
                return 0;
        }
    }

    private static CharacterClass ClassOf(char value)
    {
        if (value >= 'a' && value <= 'z')
        {
            return CharacterClass.Lower;
        }

        if (value >= 'A' && value <= 'Z')
        {
            return CharacterClass.Upper;
        }

        if (value >= '0' && value <= '9')
        {
            return CharacterClass.Digit;
        }

        if (value == ' ' || value == '\t' || value == '\r' || value == '\n')
        {
            return CharacterClass.White;
        }

        if (SqlDelimiters.IndexOf(value) >= 0)
        {
            return CharacterClass.Delimiter;
        }

        if (char.IsWhiteSpace(value))
        {
            return CharacterClass.White;
        }

        if (char.IsUpper(value))
        {
            return CharacterClass.Upper;
        }

        if (char.IsDigit(value))
        {
            return CharacterClass.Digit;
        }

        // 中日韓等沒有大小寫的文字仍應視為單字字元，否則整個識別字都會被當成分隔符。
        return char.IsLetter(value) ? CharacterClass.Lower : CharacterClass.NonWord;
    }

    /// <summary>順序有意義：<see cref="BonusFor"/> 以 &gt; NonWord 判斷是否為單字字元。</summary>
    private enum CharacterClass
    {
        White = 0,
        Delimiter = 1,
        NonWord = 2,
        Digit = 3,
        Lower = 4,
        Upper = 5
    }
}
