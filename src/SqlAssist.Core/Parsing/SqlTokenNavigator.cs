using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;

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
    /// <paramref name="index"/> 的 END 收掉一個區塊或 CASE。
    /// </summary>
    /// <remarks>
    /// 兩種 END 不收：<c>END CONVERSATION</c> 是 Service Broker 的語句，<c>GENERATED ALWAYS AS ROW END</c>
    /// 是時態表期間資料行的產生方式。當成區塊結尾的話，外層的 BEGIN 提早配對，位置分析也在那之後回報下一句。
    /// </remarks>
    public static bool ClosesBlock(IReadOnlyList<SqlToken> tokens, int index)
    {
        return tokens[index].IsKeyword("END") &&
            !(index + 1 < tokens.Count && tokens[index + 1].IsKeyword("CONVERSATION")) &&
            !(index >= 4 &&
                tokens[index - 2].IsKeyword("AS") &&
                tokens[index - 3].IsKeyword("ALWAYS") &&
                tokens[index - 4].IsKeyword("GENERATED"));
    }

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
    /// 緊接在 <c>FROM (</c> 之後時也開啟查詢：可組合 DML（<c>FROM (MERGE … OUTPUT …) AS d</c>）的結果是 OUTPUT 的資料列。
    /// </summary>
    /// <remarks>
    /// 只認 FROM 之後：別處的 <c>(INSERT</c> 是權限清單（資料庫稽核規格的 <c>ADD (INSERT ON …</c>），不是查詢。
    /// 不認的症狀是括號裡的 DML 併進外層，<c>AS d (c1, c2) WHERE </c> 列的是 DML 目標的欄位，不是 <c>d</c> 與它的資料行。
    /// </remarks>
    private static readonly HashSet<string> ComposableDmlKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "INSERT", "UPDATE", "DELETE", "MERGE"
        };

    /// <summary>
    /// <paramref name="open"/> 的左括號後面是不是一個查詢。
    /// </summary>
    /// <remarks>
    /// 巢狀括號要看穿：<c>((SELECT …))</c> 的外層也是查詢的開頭。但只在裡面那一組就是整個查詢時——
    /// 關上之後接的是右括號或集合運算子；接別名或聯結的話（<c>((SELECT …) AS s JOIN Copy ON …)</c>），
    /// 外層只是把一段聯結括起來，裡面的來源屬於外面那一層。當成查詢的症狀是 <c>ON</c> 之後
    /// 一個別名都不列，範圍停在外層括號裡，而那裡沒有 FROM。用迴圈而不是遞迴——
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

        if (!(next < tokens.Count
            && tokens[next].Kind == SqlTokenKind.Identifier
            && !tokens[next].IsQuoted
            && (QueryKeywords.Contains(tokens[next].Value) ||
                next == open + 1 && open > 0 && tokens[open - 1].IsKeyword("FROM") && IsComposableDml(tokens[next]))))
        {
            return false;
        }

        // 還沒關上的那一組當成查詢：游標就在裡面，後面接什麼還沒寫出來。
        for (var inner = next - 1; inner > open; inner--)
        {
            var close = FindClosingParenthesis(tokens, inner, tokens.Count);

            if (close >= 0 && close + 1 < tokens.Count && !ContinuesQuery(tokens[close + 1]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>可組合 DML 的動詞：它的結果是 OUTPUT 子句的資料列，見 <see cref="ComposableDmlKeywords"/>。</summary>
    internal static bool IsComposableDml(SqlToken token) =>
        token.Kind == SqlTokenKind.Identifier && !token.IsQuoted && ComposableDmlKeywords.Contains(token.Value);

    /// <summary>接在一組查詢括號之後、仍屬於同一個查詢運算式的詞元。</summary>
    private static bool ContinuesQuery(SqlToken token) =>
        token.IsPunctuation(")") ||
        token.IsKeyword("UNION") ||
        token.IsKeyword("EXCEPT") ||
        token.IsKeyword("INTERSECT") ||
        token.IsKeyword("ORDER");

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
    ///
    /// 省略的段也是名稱的一部分：<c>LibArchive..Loan</c> 省略了結構描述，<c>..Loan</c>、<c>.dbo.Loan</c>
    /// 連前面幾段都省略，點號之間沒有詞元。只認寫出來的段的話，<c>CREATE TABLE LibArchive..Loan (</c>
    /// 往回走停在點號上，認不出這是資料表的定義。點號前面的保留字不是名稱（<c>TABLE..Loan</c> 的
    /// <c>TABLE</c>），開頭的點號接在右括號之後時是方法呼叫（<c>(…).value(</c>），兩者都不算。
    /// </remarks>
    public static int SkipQualifiedNameBackward(IReadOnlyList<SqlToken> tokens, int last)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        var index = last;

        while (index >= 1 && tokens[index - 1].IsPunctuation("."))
        {
            var dots = index - 1;

            while (dots >= 1 && tokens[dots - 1].IsPunctuation("."))
            {
                dots--;
            }

            if (dots >= 1 && IsNamePart(tokens[dots - 1]))
            {
                index = dots - 1;
                continue;
            }

            if (dots >= 1 && tokens[dots - 1].IsPunctuation(")"))
            {
                break;
            }

            index = dots;
            break;
        }

        return index;
    }

    /// <summary>寫得進多段式名稱的一段：識別字，保留字要加引號。</summary>
    public static bool IsNamePart(SqlToken token) =>
        token.Kind == SqlTokenKind.Identifier && (token.IsQuoted || !SqlKeywordCatalog.IsKeyword(token.Value));

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

    /// <summary>
    /// 每一個位置的 <see cref="FindUnclosedParenthesis"/>，一趟算完。
    /// </summary>
    /// <remarks>
    /// 往回找一次最壞要走到指令碼開頭（沒有分號時），同一份文字問很多個位置時就是平方：
    /// 範圍分析判游標後方的語句開頭、欄位來源解析掃每一句 <c>SELECT … INTO</c> 都是這種問法。
    ///
    /// 答案與往回找的那一份相同：還開著的左括號裡最內層那一個；同一層在它之後出現過分號或
    /// 配不起來的右括號時是 -1。關上的括號連同裡面的分號一起消失。
    /// </remarks>
    public static int[] MapUnclosedParentheses(IReadOnlyList<SqlToken> tokens)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        var result = new int[tokens.Count];
        var opens = new Stack<int>();

        // 每一層（最外層加上每個還開著的左括號）有沒有出現過分號或配不起來的右括號。
        var blocked = new Stack<bool>();
        blocked.Push(false);

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];

            if (token.IsPunctuation("("))
            {
                opens.Push(index);
                blocked.Push(false);
            }
            else if (token.IsPunctuation(")"))
            {
                if (opens.Count > 0)
                {
                    opens.Pop();
                    blocked.Pop();
                }
                else
                {
                    blocked.Pop();
                    blocked.Push(true);
                }
            }
            else if (token.IsPunctuation(";"))
            {
                blocked.Pop();
                blocked.Push(true);
            }

            result[index] = opens.Count == 0 || blocked.Peek() ? -1 : opens.Peek();
        }

        return result;
    }

    /// <summary>逗號分隔的清單裡，從 <paramref name="start"/> 起下一個同層逗號；沒有時回 <paramref name="end"/>。</summary>
    /// <remarks>括號裡的逗號屬於那一組（<c>decimal(10, 2)</c>），不算。</remarks>
    public static int FindListItemEnd(IReadOnlyList<SqlToken> tokens, int start, int end)
    {
        var depth = 0;

        for (var index = start; index < end; index++)
        {
            var token = tokens[index];

            if (token.IsPunctuation("("))
            {
                depth++;
                continue;
            }

            if (token.IsPunctuation(")"))
            {
                depth--;
                continue;
            }

            if (depth == 0 && token.IsPunctuation(","))
            {
                return index;
            }
        }

        return end;
    }

    /// <summary>
    /// 從 <paramref name="index"/> 跳過一個資料型別，回傳型別之後的位置；那裡不是名稱時原樣回傳。
    /// </summary>
    /// <remarks>
    /// 型別可以帶結構描述（<c>dbo.CopyList</c>），也可以帶長度或有效位數（<c>varchar(10)</c>），
    /// 也可以是多字的 ANSI 寫法（<c>national char varying(10)</c>，見 <see cref="SqlDataTypeCatalog.CountWords"/>）。
    /// 資料行定義的每一種讀法都走這一份，各寫一份的症狀是其中一份不認得自訂型別。
    /// </remarks>
    public static int SkipDataType(IReadOnlyList<SqlToken> tokens, int index, int end)
    {
        var start = index;
        var words = SqlDataTypeCatalog.CountWords(tokens, index, end);
        index += words;

        while (words == 0 && index < end && tokens[index].Kind == SqlTokenKind.Identifier)
        {
            index++;

            if (index < end && tokens[index].IsPunctuation("."))
            {
                index++;
                continue;
            }

            break;
        }

        return index > start && index < end && tokens[index].IsPunctuation("(")
            ? SkipParenthesised(tokens, index, end)
            : index;
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
        foreach (var pair in FindParenthesisPairs(tokens, start, end))
        {
            paired[pair.Key - start] = true;
            paired[pair.Value - start] = true;
        }

        return paired;
    }

    /// <summary>一次建立括號索引，避免各呈現層與分析器重複逐端掃描。</summary>
    public static IEnumerable<KeyValuePair<int, int>> FindParenthesisPairs(
        IReadOnlyList<SqlToken> tokens, int start, int end, Func<SqlToken, bool>? reset = null)
    {
        var open = new Stack<int>();

        for (var index = start; index < end; index++)
        {
            if (reset?.Invoke(tokens[index]) == true)
            {
                open.Clear();
                continue;
            }
            if (tokens[index].IsPunctuation("("))
            {
                open.Push(index);
                continue;
            }

            if (tokens[index].IsPunctuation(")") && open.Count > 0)
            {
                yield return new KeyValuePair<int, int>(open.Pop(), index);
            }
        }

    }
}
