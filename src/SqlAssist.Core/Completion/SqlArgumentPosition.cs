using System;
using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 游標是不是停在一個「只有那一份清單合法」的封閉位置。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlDataTypePosition"/> 同一種判斷、同一個代價權衡：判定成立時整份
/// 清單就換掉，因此只收看得出來的四種，其餘一律照常。
///
/// 四種都認得出來，是因為游標前面那個字就把話說完了——
/// <c>DATEADD(</c>、<c>WITH (</c>、<c>OPTION (</c> 與 <c>COLLATE</c>。
///
/// 定序與另外三種差在清單的來源不在本機（見 <see cref="CompletionTarget.Collation"/>），
/// 但「這裡文法上要什麼」仍然只看文字，因此判斷留在這一份，不跟著清單搬去中繼資料層。
/// </remarks>
public static class SqlArgumentPosition
{
    /// <summary>第一個引數是日期部分的函式。</summary>
    private static readonly HashSet<string> DatePartFunctions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATENAME", "DATEPART", "DATETRUNC"
        };

    /// <summary>
    /// 判斷 <paramref name="tokens"/> 的尾端之後是哪一種封閉清單的位置。
    /// </summary>
    /// <param name="tokens">游標<b>之前</b>、不含正在輸入的那個詞元的詞法單元。</param>
    /// <param name="target">判定成立時的建議目標。</param>
    public static bool TryResolve(IReadOnlyList<SqlToken> tokens, out CompletionTarget target)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        target = CompletionTarget.Any;
        var last = tokens.Count - 1;

        if (last < 0)
        {
            return false;
        }

        // COLLATE | ——三種位置（運算式之後、資料行定義、CREATE／ALTER DATABASE）
        // 前面長得都不一樣，後面要的東西卻完全一樣，所以只認 COLLATE 這個字。
        // 這一條沒有括號可以配，也不必往前多看一個詞元。
        if (tokens[last].IsKeyword("COLLATE"))
        {
            target = CompletionTarget.Collation;
            return true;
        }

        // DATEADD(| ——只有第一個引數；打過逗號之後那裡要的是數字與日期。
        if (tokens[last].IsPunctuation("(") &&
            last >= 1 &&
            IsBareIdentifier(tokens[last - 1]) &&
            DatePartFunctions.Contains(tokens[last - 1].Value))
        {
            target = CompletionTarget.DatePart;
            return true;
        }

        // WITH (| 與 WITH (NOLOCK, | ——提示是一份清單，逗號之後還是提示。
        if (!tokens[last].IsPunctuation("(") && !tokens[last].IsPunctuation(","))
        {
            return false;
        }

        var open = SqlTokenNavigator.FindUnclosedParenthesis(tokens, last);

        if (open < 1 || !IsBareIdentifier(tokens[open - 1]))
        {
            return false;
        }

        // CTE 的 WITH 後面接的是名稱（;WITH c AS (…)），中間隔著那個名稱，
        // 所以「WITH 緊接著左括號」在這裡只會是資料表提示。
        if (tokens[open - 1].IsKeyword("WITH"))
        {
            target = CompletionTarget.TableHint;
            return true;
        }

        if (tokens[open - 1].IsKeyword("OPTION"))
        {
            target = CompletionTarget.QueryHint;
            return true;
        }

        return false;
    }

    private static bool IsBareIdentifier(SqlToken token)
    {
        return token.Kind == SqlTokenKind.Identifier && !token.IsQuoted;
    }
}
