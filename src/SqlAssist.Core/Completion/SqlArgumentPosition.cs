using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 游標是不是停在一個「只有那一份清單合法」的封閉位置。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlDataTypePosition"/> 同一種判斷、同一個代價權衡：判定成立時整份
/// 清單就換掉，因此只收看得出來的幾種，其餘一律照常。
///
/// 都認得出來，是因為游標前面那個字就把話說完了——
/// <c>DATEADD(</c>、<c>WITH (</c>、<c>OPTION (</c>、<c>TABLE HINT (t,</c> 與 ODBC 的 <c>{fn</c>。清單只有伺服器知道的那幾種
/// （<c>COLLATE</c>、<c>SET LANGUAGE</c>、<c>AT TIME ZONE</c>）由 <see cref="SqlInstanceList"/> 判斷。
/// </remarks>
public static class SqlArgumentPosition
{
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

        // DATEADD(|、DATE_BUCKET(| ——只有第一個引數；打過逗號之後那裡要的是數字與日期。哪些函式由簽章說。
        if (tokens[last].IsPunctuation("(") &&
            last >= 1 &&
            IsBareIdentifier(tokens[last - 1]) &&
            SqlFunctionCatalog.FirstParameterIs(tokens[last - 1].Value, "datepart"))
        {
            target = CompletionTarget.DatePart;
            return true;
        }

        // {fn | ——ODBC 跳脫只收它自己那一份純量函式。
        if (last >= 1 && tokens[last].IsKeyword("fn") && tokens[last - 1] is { Kind: SqlTokenKind.Operator, Value: "{" })
        {
            target = CompletionTarget.OdbcFunction;
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

        // OPTION (TABLE HINT (c, | ——第一個引數是資料表或別名，逗號之後每一項都是資料表提示，與 WITH ( 同一份清單。
        if (tokens[last].IsPunctuation(",") && open >= 2 && tokens[open - 1].IsKeyword("HINT") && tokens[open - 2].IsKeyword("TABLE"))
        {
            target = CompletionTarget.TableHint;
            return true;
        }

        return false;
    }

    private static bool IsBareIdentifier(SqlToken token)
    {
        return token.Kind == SqlTokenKind.Identifier && !token.IsQuoted;
    }
}
