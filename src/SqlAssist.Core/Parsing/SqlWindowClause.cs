using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 視窗規格與具名視窗：<c>OVER (…)</c>、<c>OVER w</c>，以及查詢的 <c>WINDOW w AS (…), w2 AS (w …)</c>。
/// </summary>
/// <remarks>
/// 位置分析（括號裡接 PARTITION、ORDER 與框架）、視窗名稱那一格與名稱的名冊問的是同一件事：
/// 哪一個左括號開啟一份視窗規格。各認一份的話，WINDOW 子句的括號在一邊是視窗、在另一邊是運算式。
/// </remarks>
public static class SqlWindowClause
{
    /// <summary><paramref name="open"/> 的左括號開啟一份視窗規格：<c>OVER (</c>，或 WINDOW 子句一項的 <c>w AS (</c>。</summary>
    public static bool OpensSpecification(IReadOnlyList<SqlToken> tokens, int open)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        return IsOpen(tokens, open) && (IsOver(tokens, open - 1) || OpensDefinition(tokens, open));
    }

    /// <summary>
    /// <paramref name="open"/> 開啟 WINDOW 子句一項的定義：前面是 <c>WINDOW w AS</c>，或上一項寫完之後的 <c>, w AS</c>。
    /// </summary>
    /// <remarks>
    /// CTE 的 <c>WITH c AS (…), d AS (</c> 長得一樣，差在清單的開頭：往回一路是 <c>, 名稱 AS (…)</c>，
    /// 走到 WINDOW 才算。
    /// </remarks>
    public static bool OpensDefinition(IReadOnlyList<SqlToken> tokens, int open)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        return IsOpen(tokens, open) && open >= 3 && tokens[open - 1].IsKeyword("AS") && NamesDefinition(tokens, open - 2);
    }

    /// <summary><paramref name="name"/> 是 WINDOW 子句一項的名稱：前面是 <c>WINDOW</c>，或上一項寫完之後的逗號。</summary>
    public static bool NamesDefinition(IReadOnlyList<SqlToken> tokens, int name)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        return name >= 1 &&
            name < tokens.Count &&
            tokens[name].Kind == SqlTokenKind.Identifier &&
            (tokens[name - 1].IsKeyword("WINDOW") || EndsDefinition(tokens, name - 1));
    }

    /// <summary><paramref name="comma"/> 是 WINDOW 子句一項寫完之後的逗號：之後是下一個視窗的新名字。</summary>
    public static bool EndsDefinition(IReadOnlyList<SqlToken> tokens, int comma)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        return comma >= 1 &&
            comma < tokens.Count &&
            tokens[comma].IsPunctuation(",") &&
            tokens[comma - 1].IsPunctuation(")") &&
            OpensDefinition(tokens, SqlTokenNavigator.FindOpeningParenthesis(tokens, comma - 1));
    }

    /// <summary>
    /// <paramref name="previous"/> 之後那一格寫得出既有的視窗名稱：<c>OVER </c>，或視窗規格的左括號（基底視窗）。
    /// </summary>
    public static bool IntroducesReference(IReadOnlyList<SqlToken> tokens, int previous)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        return previous >= 0 && previous < tokens.Count && (IsOver(tokens, previous) || OpensSpecification(tokens, previous));
    }

    /// <summary>
    /// <paramref name="start"/> 到 <paramref name="end"/>（詞元索引，不含）這段查詢的 WINDOW 子句取的名稱，依出現順序、不重複。
    /// </summary>
    /// <param name="caretPosition">游標所在那一項不算：視窗不能以自己為基底。</param>
    /// <remarks>
    /// 只看這一層：子查詢的 WINDOW 子句屬於它自己。深度只算配對得起來的括號，游標所在、還沒關上的
    /// <c>OVER (</c> 不會把後面的 WINDOW 子句推到下一層。
    /// </remarks>
    public static IReadOnlyList<string> CollectNames(IReadOnlyList<SqlToken> tokens, int start, int end, int caretPosition)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        start = Math.Max(start, 0);
        end = Math.Min(end, tokens.Count);

        List<string>? names = null;
        HashSet<string>? seen = null;
        var paired = SqlTokenNavigator.FindPairedParentheses(tokens, start, end);
        var depth = 0;

        for (var index = start; index < end; index++)
        {
            var token = tokens[index];

            if (depth == 0 && OpensDefinition(tokens, index) && !Contains(tokens, index, end, caretPosition))
            {
                var name = tokens[index - 2].Value;

                if ((seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(name))
                {
                    (names ??= new List<string>()).Add(name);
                }
            }

            if (paired[index - start])
            {
                depth += token.IsPunctuation("(") ? 1 : -1;
            }
        }

        return (IReadOnlyList<string>?)names ?? Array.Empty<string>();
    }

    private static bool IsOpen(IReadOnlyList<SqlToken> tokens, int open) =>
        open >= 1 && open < tokens.Count && tokens[open].IsPunctuation("(");

    /// <summary>函式呼叫之後的 <c>OVER</c>（<c>SUM(a) OVER</c>、<c>WITHIN GROUP (…) OVER</c>）。</summary>
    private static bool IsOver(IReadOnlyList<SqlToken> tokens, int index) =>
        index >= 1 && tokens[index].IsKeyword("OVER") && tokens[index - 1].IsPunctuation(")");

    /// <summary>游標在 <paramref name="open"/> 開啟的那一組括號裡。</summary>
    private static bool Contains(IReadOnlyList<SqlToken> tokens, int open, int end, int caretPosition)
    {
        if (caretPosition <= tokens[open].Start)
        {
            return false;
        }

        var close = SqlTokenNavigator.FindClosingParenthesis(tokens, open, end);
        return close < 0 || caretPosition <= tokens[close].Start;
    }
}
