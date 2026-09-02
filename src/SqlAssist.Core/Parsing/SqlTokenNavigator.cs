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
    /// <summary>
    /// 緊接在左括號後面時，代表這個括號開啟了一個新的查詢範圍。
    /// </summary>
    /// <remarks>
    /// 括號在 T-SQL 裡絕大多數時候只是運算式的一部分——函式呼叫、
    /// 運算優先權、<c>IN</c> 清單、資料行清單。只有這三個字後面跟著的
    /// 才是自己帶 FROM 子句的查詢。
    /// </remarks>
    private static readonly HashSet<string> QueryKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "WITH", "VALUES"
        };

    /// <summary>
    /// <paramref name="open"/> 的左括號後面是不是一個查詢。
    /// </summary>
    /// <remarks>
    /// 巢狀括號要看穿：<c>((SELECT …))</c> 的外層也是查詢的開頭。用迴圈而不是遞迴——
    /// 一份全是左括號的文字不該讓分析器把堆疊用完。
    ///
    /// 與括號配對放在一起是因為兩者永遠一起用：範圍分析要它區分子查詢與
    /// <c>COUNT(</c>，位置分析要它區分衍生資料表與函式引數。各寫一份的話，
    /// 其中一邊多認得一個開頭關鍵字，另一邊就會對同一段文字得到不同的答案。
    /// </remarks>
    public static bool OpensQuery(IReadOnlyList<SqlToken> tokens, int open)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        var next = open + 1;

        while (next < tokens.Count && tokens[next].IsPunctuation("("))
        {
            next++;
        }

        return next < tokens.Count
            && tokens[next].Kind == SqlTokenKind.Identifier
            && !tokens[next].IsQuoted
            && QueryKeywords.Contains(tokens[next].Value);
    }

    /// <summary>
    /// 從一個限定名稱的<b>最後一個</b>詞元往回走到它的第一個詞元。
    /// </summary>
    /// <remarks>
    /// <c>dbo.Lib_Reader</c> 是一個資料來源而不是兩個，所以往回數「名稱單位」的
    /// 每一處都得先跳過點號。各寫一份的症狀是其中一份只認得簡名——
    /// <c>FROM Lib_Reader </c> 判得出別名位置，<c>FROM dbo.Lib_Reader </c> 卻判不出來。
    ///
    /// <paramref name="last"/> 不是識別字時原樣回傳：呼叫端要問的是位置，
    /// 而不是「這裡有沒有名稱」。
    /// </remarks>
    public static int SkipQualifiedNameBackward(IReadOnlyList<SqlToken> tokens, int last)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        var index = last;

        while (index >= 2 &&
               tokens[index - 1].IsPunctuation(".") &&
               tokens[index - 2].Kind == SqlTokenKind.Identifier)
        {
            index -= 2;
        }

        return index;
    }

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

    /// <summary>
    /// 從 <paramref name="from"/> 起往回找出「還沒關上」的那個左括號；沒有就回傳 -1。
    /// </summary>
    /// <remarks>
    /// 使用者正在打的呼叫或清單一定是還開著的那一個，因此
    /// <c>CONVERT(</c>、<c>WITH (NOLOCK, </c>、<c>CREATE TABLE t (Id INT, </c>
    /// 這些位置問的都是同一個問題。途中關得起來的括號整組跳過（那是引數自己的），
    /// 分號代表前一個敘述已經結束。
    /// </remarks>
    public static int FindUnclosedParenthesis(IReadOnlyList<SqlToken> tokens, int from)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        for (var index = Math.Min(from, tokens.Count - 1); index >= 0; index--)
        {
            var token = tokens[index];

            if (token.IsPunctuation(")"))
            {
                var open = FindOpeningParenthesis(tokens, index);

                if (open < 0)
                {
                    return -1;
                }

                index = open;
                continue;
            }

            if (token.IsPunctuation(";"))
            {
                return -1;
            }

            if (token.IsPunctuation("("))
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
