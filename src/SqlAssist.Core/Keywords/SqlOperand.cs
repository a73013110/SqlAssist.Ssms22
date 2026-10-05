using System;
using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Keywords;

/// <summary>運算元與運算式的邊界：只看詞元判得出的那幾種。</summary>
/// <remarks>
/// 位置分析、型別位置與片語的 <c>{value}</c> 問的是同一件事——這裡寫完一個值了沒有、那個值從哪裡開始。
/// 各認一份的症狀是一邊認得 <c>[Name]</c>、另一邊認不得；片語只認單一常值時，<c>FETCH NEXT @a - @b + 1 ROWS </c>
/// 之後列不出 <c>ONLY</c>，而值的格子在文法上本來就是運算式。
/// </remarks>
public static class SqlOperand
{
    /// <summary>
    /// <paramref name="index"/> 這個詞元寫完一個運算元：名稱、變數、常值、右括號，或 <c>NULL</c> 這種自成一項的關鍵字。
    /// </summary>
    public static bool Ends(IReadOnlyList<SqlToken> tokens, int index)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        var token = tokens[index];

        return token.Kind switch
        {
            SqlTokenKind.Identifier => token.IsQuoted ||
                (index >= 1 && tokens[index - 1].IsPunctuation(".")) ||
                !SqlKeywordCatalog.IsKeyword(token.Value) ||
                SqlKeywordCatalog.EndsItem(token.Value),
            SqlTokenKind.Variable or SqlTokenKind.Number or SqlTokenKind.String => true,
            _ => token.IsPunctuation(")")
        };
    }

    /// <summary>
    /// 從 <paramref name="last"/> 往回走過一個運算式，回傳它第一個詞元的索引；<paramref name="last"/> 寫不完運算元時回傳 -1。
    /// </summary>
    /// <remarks>
    /// 運算元是常值、變數、名稱（含限定字）、函式呼叫或一整組括號，以算術運算子串起來，開頭可以帶正負號；
    /// 運算元之後可以接 <c>AT TIME ZONE</c> 與時區（<c>DEFAULT @d AT TIME ZONE @z</c>）。
    /// 比較與邏輯運算子不算：<c>WHERE a = 1</c> 的值是 <c>1</c>，等號是選項的指派（<c>SIZE = 5</c>）時也一樣。
    /// <c>CASE … END</c> 不走：END 之前一直到 CASE 都可能有逗號以外的任何東西，認不出來就不比對，只是少列字。
    /// </remarks>
    public static int SkipBackward(IReadOnlyList<SqlToken> tokens, int last)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        var index = last;

        while (true)
        {
            var start = SkipOperandBackward(tokens, index);

            if (start < 0)
            {
                return -1;
            }

            var before = start - 1;

            if (before >= 1 && IsArithmetic(tokens[before]) && Ends(tokens, before - 1))
            {
                index = before - 1;
                continue;
            }

            if (EndsTimeZone(tokens, before) && Ends(tokens, before - 3))
            {
                index = before - 3;
                continue;
            }

            return before >= 0 && IsSign(tokens[before]) ? before : start;
        }
    }

    private static int SkipOperandBackward(IReadOnlyList<SqlToken> tokens, int last)
    {
        if (last < 0 || !Ends(tokens, last) || tokens[last].IsKeyword("END"))
        {
            return -1;
        }

        if (!tokens[last].IsPunctuation(")"))
        {
            return tokens[last].Kind == SqlTokenKind.Identifier
                ? SqlTokenNavigator.SkipQualifiedNameBackward(tokens, last)
                : last;
        }

        var open = SqlTokenNavigator.FindOpeningParenthesis(tokens, last);

        if (open < 0)
        {
            return -1;
        }

        // 括號前面是函式名稱就是一次呼叫；COALESCE、CONVERT 是關鍵字，也是內建函式。
        return open >= 1 && tokens[open - 1] is { Kind: SqlTokenKind.Identifier } name &&
            (Ends(tokens, open - 1) || SqlFunctionCatalog.TryGetSignature(name.Value, out _))
            ? SqlTokenNavigator.SkipQualifiedNameBackward(tokens, open - 1)
            : open;
    }

    /// <summary><paramref name="index"/> 寫完運算式的後綴 <c>AT TIME ZONE</c>：時區之前是被轉換的那個運算式。</summary>
    private static bool EndsTimeZone(IReadOnlyList<SqlToken> tokens, int index) =>
        index >= 3 && tokens[index].IsKeyword("ZONE") && tokens[index - 1].IsKeyword("TIME") && tokens[index - 2].IsKeyword("AT");

    private static bool IsArithmetic(SqlToken token) =>
        token.Kind == SqlTokenKind.Operator && token.Value is "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^";

    private static bool IsSign(SqlToken token) =>
        token.Kind == SqlTokenKind.Operator && token.Value is "+" or "-";
}
