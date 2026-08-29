using System;
using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Wildcards;

/// <summary>
/// 判斷游標前方的 <c>*</c> 是不是可以展開成欄位清單的萬用字元，並解析它的欄位來源。
/// </summary>
/// <remarks>
/// 只做一件事，而且只看文字：<b>這個星號是不是萬用字元。</b><c>*</c> 在 T-SQL 裡
/// 絕大多數時候是乘號，唯一的判斷依據是它前面接什麼——選取清單的開頭
/// （<c>SELECT</c> 與它的 <c>DISTINCT</c>、<c>TOP n</c> 前置詞）或同一份選取清單裡的
/// 逗號。<c>COUNT(*)</c> 前面是左括號，<c>a * b</c> 前面是識別字，兩者都不算。
///
/// 「欄位從哪裡來」交給 <see cref="SqlColumnSourceResolver"/>，那一份與建議清單的
/// 欄位建議共用：各寫一份的話，同一個衍生資料表會在展開時攤得開、在建議清單裡
/// 卻一個欄位都列不出來。
///
/// 任何一個來源解析不出來就整個放棄（回傳 null），不做部分展開：
/// 少了幾個欄位的 <c>SELECT</c> 仍然可以執行，卻執行出錯的結果，
/// 那比什麼都不做糟糕得多。
/// </remarks>
public static class SqlWildcardAnalyzer
{
    /// <summary>
    /// 分析游標前方的萬用字元。
    /// </summary>
    /// <returns>
    /// 可以展開時回傳展開所需的全部資訊；游標不在萬用字元後方，
    /// 或任何一個欄位來源解析不出來時為 null。
    /// </returns>
    public static SqlWildcardTarget? Analyze(string sql, int caretPosition)
    {
        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        if (caretPosition < 0 || caretPosition > sql.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(caretPosition));
        }

        // 這條路徑掛在游標移動上，所以先用一次字元比較擋掉絕大多數的呼叫。
        // 真的要分析時才付詞法分析的成本。
        if (caretPosition == 0 || sql[caretPosition - 1] != '*')
        {
            return null;
        }

        var tokens = SqlTokenizer.Tokenize(sql);
        var wildcard = FindWildcard(tokens, caretPosition);

        if (wildcard < 0)
        {
            return null;
        }

        var start = tokens[wildcard].Start;
        string? qualifierText = null;
        string? qualifier = null;
        var previous = wildcard - 1;

        // a.* 與 dbo.PUBLISHER.*：限定字連同點號都要一起被換掉。
        if (previous >= 1 && tokens[previous].IsPunctuation(".") && tokens[previous - 1].Kind == SqlTokenKind.Identifier)
        {
            qualifier = tokens[previous - 1].Value;
            previous -= 2;

            // 多段限定字往前吃到底，起點才會落在 dbo 而不是 PUBLISHER 上。
            while (previous >= 1 && tokens[previous].IsPunctuation(".") && tokens[previous - 1].Kind == SqlTokenKind.Identifier)
            {
                previous -= 2;
            }

            start = tokens[previous + 1].Start;

            // 取到最後一個點號之前的識別字為止，中間的空白照使用者寫的保留。
            qualifierText = sql.Substring(start, tokens[wildcard - 2].End - start);
        }

        if (!IsSelectListPosition(tokens, previous))
        {
            return null;
        }

        var scope = SqlScopeAnalyzer.Analyze(tokens, caretPosition);

        if (scope.Tables.Count == 0)
        {
            return null;
        }

        var references = new List<SqlTableReference>();

        if (qualifier is null)
        {
            references.AddRange(scope.Tables);
        }
        else if (scope.TryResolve(qualifier, out var resolved))
        {
            references.Add(resolved);
        }
        else
        {
            return null;
        }

        var sources = new SqlColumnSourceResolver(tokens).ResolveAll(references);

        if (sources is null)
        {
            return null;
        }

        return new SqlWildcardTarget(
            start,
            caretPosition - start,
            qualifierText,
            qualify: qualifierText is not null || references.Count > 1,
            sources);
    }

    /// <summary>找出結尾正好落在游標上的那個星號。</summary>
    private static int FindWildcard(IReadOnlyList<SqlToken> tokens, int caretPosition)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];

            if (token.End > caretPosition)
            {
                break;
            }

            // 字串與註解裡的星號根本不會成為詞法單元，因此不必另外排除。
            if (token.End == caretPosition && token.Kind == SqlTokenKind.Operator && token.Value == "*")
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// <paramref name="index"/> 之前的內容是不是「選取清單的開頭」。
    /// </summary>
    /// <remarks>
    /// 這是乘號與萬用字元唯一分得開的地方。允許的只有兩條路：一路退到
    /// <c>SELECT</c>（中間可以夾 <c>DISTINCT</c>、<c>TOP n</c>、<c>WITH TIES</c>），
    /// 或先遇到逗號、再從逗號往回確認整份清單確實由 <c>SELECT</c> 開頭。
    ///
    /// 數字刻意只在 <c>TOP</c> 後面才放行：少了這個條件，<c>SELECT 5 * 3</c>
    /// 的乘號會被當成萬用字元。
    /// </remarks>
    private static bool IsSelectListPosition(IReadOnlyList<SqlToken> tokens, int index)
    {
        if (index < 0)
        {
            return false;
        }

        if (tokens[index].IsPunctuation(","))
        {
            return StartsSelectList(tokens, index - 1);
        }

        while (index >= 0)
        {
            var token = tokens[index];

            if (token.IsKeyword("SELECT"))
            {
                return true;
            }

            if (token.Kind == SqlTokenKind.Identifier &&
                !token.IsQuoted &&
                SqlColumnSourceResolver.SelectListPrelude.Contains(token.Value))
            {
                index--;
                continue;
            }

            // TOP 10、TOP @n、TOP (@n + 1)
            if (token.Kind is SqlTokenKind.Number or SqlTokenKind.Variable)
            {
                if (index < 1 || !tokens[index - 1].IsKeyword("TOP"))
                {
                    return false;
                }

                index -= 2;
                continue;
            }

            if (token.IsPunctuation(")"))
            {
                var open = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

                if (open < 1 || !tokens[open - 1].IsKeyword("TOP"))
                {
                    return false;
                }

                index = open - 2;
                continue;
            }

            return false;
        }

        return false;
    }

    /// <summary>從選取清單中間往回確認這份清單確實由 <c>SELECT</c> 開頭。</summary>
    private static bool StartsSelectList(IReadOnlyList<SqlToken> tokens, int index)
    {
        var depth = 0;

        for (; index >= 0; index--)
        {
            var token = tokens[index];

            if (token.IsPunctuation(")"))
            {
                depth++;
                continue;
            }

            if (token.IsPunctuation("("))
            {
                // 深度已經是 0 卻遇到左括號，代表這串逗號是引數或資料行清單，
                // 不是選取清單：INSERT INTO t (a, *) 的星號不該展開。
                if (depth == 0)
                {
                    return false;
                }

                depth--;
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            if (token.IsKeyword("SELECT"))
            {
                return true;
            }

            if (token.IsPunctuation(";"))
            {
                return false;
            }

            if (token.Kind == SqlTokenKind.Identifier &&
                !token.IsQuoted &&
                SqlColumnSourceResolver.SelectListTerminators.Contains(token.Value))
            {
                return false;
            }
        }

        return false;
    }
}
