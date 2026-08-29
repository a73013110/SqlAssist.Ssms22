using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 在詞法單元串流上找括號的配對。
/// </summary>
/// <remarks>
/// Scope 分析、萬用字元分析與 CTE 解析都要在括號之間跳來跳去，而編輯中的敘述
/// 括號幾乎總是不成對——「找不到配對時該停在哪裡」各寫一份，就會出現同一段文字
/// 在 <c>SELECT *</c> 展得開、Scope 卻解析不出資料表這種不一致。
/// </remarks>
public static class SqlTokenNavigator
{
    /// <summary>從 <paramref name="open"/> 起找出對應的右括號；配不起來時回傳 -1。</summary>
    public static int FindClosingParenthesis(IReadOnlyList<SqlToken> tokens, int open, int end)
    {
        var depth = 0;

        for (var index = Math.Max(open, 0); index < end; index++)
        {
            if (tokens[index].IsPunctuation("("))
            {
                depth++;
                continue;
            }

            if (tokens[index].IsPunctuation(")") && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>從 <paramref name="close"/> 起往回找出對應的左括號；配不起來時回傳 -1。</summary>
    public static int FindOpeningParenthesis(IReadOnlyList<SqlToken> tokens, int close)
    {
        var depth = 0;

        for (var index = Math.Min(close, tokens.Count - 1); index >= 0; index--)
        {
            if (tokens[index].IsPunctuation(")"))
            {
                depth++;
                continue;
            }

            if (tokens[index].IsPunctuation("(") && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>從左括號跳到對應的右括號之後；配不起來時停在 <paramref name="end"/>。</summary>
    public static int SkipParenthesised(IReadOnlyList<SqlToken> tokens, int index, int end)
    {
        var close = FindClosingParenthesis(tokens, index, end);
        return close < 0 ? end : close + 1;
    }

    /// <summary>
    /// 標出範圍內每一個「配對得起來」的括號。
    /// </summary>
    /// <remarks>
    /// 回傳的索引以 <paramref name="start"/> 為原點。沒有配對的括號留 false，
    /// 呼叫端才能把「使用者才剛打開、還沒關起來」的括號與完整的子查詢分開處理。
    /// </remarks>
    public static bool[] FindPairedParentheses(IReadOnlyList<SqlToken> tokens, int start, int end)
    {
        var paired = new bool[Math.Max(0, end - start)];
        var open = new Stack<int>();

        for (var index = start; index < end; index++)
        {
            if (tokens[index].IsPunctuation("("))
            {
                open.Push(index);
                continue;
            }

            if (tokens[index].IsPunctuation(")") && open.Count > 0)
            {
                paired[open.Pop() - start] = true;
                paired[index - start] = true;
            }
        }

        return paired;
    }
}
