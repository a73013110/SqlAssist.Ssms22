using System;
using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core;

/// <summary>
/// 判斷游標落在 <see cref="SqlKeywordPosition"/> 的哪一個位置。
/// </summary>
/// <remarks>
/// 與 <c>tools/Generate-Keywords.ps1</c> 的樣板是一對的：產生器決定「哪些關鍵字
/// 可以出現在這個位置」，這裡決定「游標現在在哪個位置」。兩邊的粒度必須一致，
/// 因此樣板一律切在前一個詞元之後，這裡也只看前一個詞元加上最近的子句關鍵字。
///
/// 判不出來時回傳 <see cref="SqlKeywordPosition.Any"/>。分不出位置的代價是清單多幾個字，
/// 猜錯位置的代價是使用者要的關鍵字消失——後者嚴重得多。
/// </remarks>
public static class SqlKeywordPositionAnalyzer
{
    /// <summary>前一個詞元是這些關鍵字時，位置可以直接決定。</summary>
    private static readonly Dictionary<string, SqlKeywordPosition> AfterKeyword =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // GO 是批次分隔符，之後必然是新批次的開頭。
            ["GO"] = SqlKeywordPosition.StatementStart,

            ["SELECT"] = SqlKeywordPosition.SelectList,

            ["FROM"] = SqlKeywordPosition.DataSource,
            ["JOIN"] = SqlKeywordPosition.DataSource,
            ["INTO"] = SqlKeywordPosition.DataSource,
            ["UPDATE"] = SqlKeywordPosition.DataSource,
            ["APPLY"] = SqlKeywordPosition.DataSource,

            ["WHERE"] = SqlKeywordPosition.Predicate,
            ["ON"] = SqlKeywordPosition.Predicate,
            ["HAVING"] = SqlKeywordPosition.Predicate,
            ["WHEN"] = SqlKeywordPosition.Predicate,
            ["AND"] = SqlKeywordPosition.Predicate,
            ["OR"] = SqlKeywordPosition.Predicate,
            ["NOT"] = SqlKeywordPosition.Predicate,

            ["ORDER"] = SqlKeywordPosition.ByAnchor,
            ["GROUP"] = SqlKeywordPosition.ByAnchor,

            ["CREATE"] = SqlKeywordPosition.DdlObject,
            ["ALTER"] = SqlKeywordPosition.DdlObject,
            ["DROP"] = SqlKeywordPosition.DdlObject,

            ["BEGIN"] = SqlKeywordPosition.BlockStart,
            ["SET"] = SqlKeywordPosition.SetTarget,
            ["INSERT"] = SqlKeywordPosition.InsertTarget
        };

    /// <summary>
    /// 前一個詞元是識別字時，往回找最近的子句關鍵字來決定位置。
    /// </summary>
    /// <remarks>
    /// <c>BY</c> 不在這裡：它要看再前面是 ORDER 還是 GROUP，單獨出現沒有意義。
    /// </remarks>
    private static readonly Dictionary<string, SqlKeywordPosition> ClauseAnchors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["SELECT"] = SqlKeywordPosition.SelectListTail,

            ["FROM"] = SqlKeywordPosition.TableSourceTail,
            ["JOIN"] = SqlKeywordPosition.TableSourceTail,
            ["INTO"] = SqlKeywordPosition.TableSourceTail,
            ["UPDATE"] = SqlKeywordPosition.TableSourceTail,
            ["APPLY"] = SqlKeywordPosition.TableSourceTail,

            ["WHERE"] = SqlKeywordPosition.ExpressionTail,
            ["ON"] = SqlKeywordPosition.ExpressionTail,
            ["HAVING"] = SqlKeywordPosition.ExpressionTail,
            ["SET"] = SqlKeywordPosition.ExpressionTail
        };

    /// <summary>
    /// 分析游標所在的位置。
    /// </summary>
    /// <param name="textBeforeToken">
    /// 游標前方的文字，且不含正在輸入的那個詞元——位置由「前一個完整的詞元」決定，
    /// 打到一半的字不算數。
    /// </param>
    public static SqlKeywordPosition Analyze(string textBeforeToken)
    {
        if (textBeforeToken is null)
        {
            throw new ArgumentNullException(nameof(textBeforeToken));
        }

        var tokens = SqlTokenizer.Tokenize(textBeforeToken);

        if (tokens.Count == 0)
        {
            return SqlKeywordPosition.StatementStart;
        }

        var last = tokens[tokens.Count - 1];

        if (last.IsPunctuation(";"))
        {
            return SqlKeywordPosition.StatementStart;
        }

        // SELECT a, | 與 ORDER BY a, | 都還在原來的子句裡，往回找就對了。
        if (last.IsPunctuation(","))
        {
            return FindClausePosition(tokens, tokens.Count - 1);
        }

        if (last.Kind is SqlTokenKind.Punctuation or SqlTokenKind.Operator)
        {
            return SqlKeywordPosition.Any;
        }

        // 加引號的識別字是名稱不是關鍵字：[FROM] 之後不是資料來源位置。
        if (last.Kind == SqlTokenKind.Identifier && !last.IsQuoted)
        {
            if (AfterKeyword.TryGetValue(last.Value, out var position))
            {
                return position;
            }

            if (IsOrderOrGroupBy(tokens, tokens.Count - 1))
            {
                // ORDER BY | 要的是欄位，不是關鍵字。
                return SqlKeywordPosition.Any;
            }

            if (SqlKeywordCatalog.IsKeyword(last.Value))
            {
                // 認得但沒有對應位置的關鍵字（THEN、ELSE、AS…），不猜。
                return SqlKeywordPosition.Any;
            }
        }

        return FindClausePosition(tokens, tokens.Count - 1);
    }

    /// <summary>往回找最近的子句關鍵字。</summary>
    private static SqlKeywordPosition FindClausePosition(IReadOnlyList<SqlToken> tokens, int from)
    {
        for (var index = from; index >= 0; index--)
        {
            var token = tokens[index];

            if (token.Kind != SqlTokenKind.Identifier || token.IsQuoted)
            {
                // 分號代表前一個敘述已經結束，再往回找只會找到別的敘述的子句。
                if (token.IsPunctuation(";"))
                {
                    return SqlKeywordPosition.StatementStart;
                }

                continue;
            }

            if (IsOrderOrGroupBy(tokens, index))
            {
                return SqlKeywordPosition.OrderByTail;
            }

            if (ClauseAnchors.TryGetValue(token.Value, out var position))
            {
                return position;
            }
        }

        return SqlKeywordPosition.Any;
    }

    /// <summary><paramref name="index"/> 是不是 ORDER BY／GROUP BY 的那個 BY。</summary>
    private static bool IsOrderOrGroupBy(IReadOnlyList<SqlToken> tokens, int index)
    {
        if (index < 1 || !tokens[index].IsKeyword("BY"))
        {
            return false;
        }

        var previous = tokens[index - 1];
        return previous.IsKeyword("ORDER") || previous.IsKeyword("GROUP");
    }
}
