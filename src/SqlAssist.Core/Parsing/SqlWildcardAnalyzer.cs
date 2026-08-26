using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 判斷游標前方的 <c>*</c> 是不是可以展開成欄位清單的萬用字元，並解析它的欄位來源。
/// </summary>
/// <remarks>
/// 只做兩件事，兩件都只看文字：
///
/// <list type="number">
/// <item>
/// <b>這個星號是不是萬用字元。</b><c>*</c> 在 T-SQL 裡絕大多數時候是乘號。
/// 唯一的判斷依據是它前面接什麼——選取清單的開頭（<c>SELECT</c> 與它的
/// <c>DISTINCT</c>、<c>TOP n</c> 前置詞）或同一份選取清單裡的逗號。
/// <c>COUNT(*)</c> 前面是左括號，<c>a * b</c> 前面是識別字，兩者都不算。
/// </item>
/// <item>
/// <b>欄位從哪裡來。</b>資料表與檢視交給中繼資料層，子查詢與 CTE 的輸出欄位
/// 則直接讀它們的選取清單——那份名單就寫在指令碼裡。內層自己又是 <c>*</c> 時
/// 遞迴下去，把最外層的別名一路帶著走。
/// </item>
/// </list>
///
/// 任何一個來源解析不出來就整個放棄（回傳 null），不做部分展開：
/// 少了幾個欄位的 <c>SELECT</c> 仍然可以執行，卻執行出錯的結果，
/// 那比什麼都不做糟糕得多。
/// </remarks>
public static class SqlWildcardAnalyzer
{
    /// <summary>子查詢與 CTE 的巢狀深度上限。</summary>
    /// <remarks>
    /// 遞迴 CTE 已經另外用「正在展開的名稱」擋掉了，這個上限是為了病態的輸入：
    /// 一份互相參照的 CTE 或幾百層的衍生資料表不該讓分析器把堆疊用完。
    /// 八層在真實的指令碼裡遠遠夠用。
    /// </remarks>
    private const int MaximumDepth = 8;

    /// <summary>可以夾在 <c>SELECT</c> 與選取清單之間的字。</summary>
    private static readonly HashSet<string> SelectListPrelude =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "DISTINCT", "ALL", "PERCENT", "TIES", "WITH"
        };

    /// <summary>出現在選取清單裡就代表「這裡已經不是選取清單」的字。</summary>
    /// <remarks>
    /// 往回找 <c>SELECT</c> 時用它當煞車：<c>ORDER BY a, *</c> 的逗號往回走會先
    /// 遇到 <c>BY</c>，那時就該停手，而不是一路走到更前面某個 <c>SELECT</c> 去。
    /// </remarks>
    private static readonly HashSet<string> SelectListTerminators =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "BY", "INTO", "SET",
            "VALUES", "ON", "USING", "WHEN", "THEN", "ELSE", "END", "UNION",
            "EXCEPT", "INTERSECT", "OPTION", "FOR", "PIVOT", "UNPIVOT", "JOIN",
            "APPLY", "INSERT", "UPDATE", "DELETE", "MERGE", "EXEC", "EXECUTE",
            "DECLARE", "CREATE", "ALTER", "DROP", "IF", "WHILE", "BEGIN",
            "RETURN", "PRINT", "USE", "OUTPUT", "TABLE", "AS", "GO"
        };

    /// <summary>選取清單到這些字為止。</summary>
    private static readonly HashSet<string> SelectListEnd =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "INTO", "UNION", "EXCEPT", "INTERSECT", "ORDER", "GROUP",
            "HAVING", "WHERE", "OPTION", "FOR"
        };

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

        var ctes = CollectCommonTableExpressions(tokens);
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

        var sources = new List<SqlWildcardColumnSource>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in references)
        {
            if (!TryResolveReference(reference, tokens, ctes, reference.EffectiveName, 0, visiting, sources))
            {
                return null;
            }
        }

        if (sources.Count == 0)
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

            if (token.Kind == SqlTokenKind.Identifier && !token.IsQuoted && SelectListPrelude.Contains(token.Value))
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
                var open = FindOpeningParenthesis(tokens, index);

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
                SelectListTerminators.Contains(token.Value))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 把一個資料來源攤平成欄位來源。
    /// </summary>
    /// <param name="qualifier">展開後要補在欄位前面的名稱，一路由最外層帶下來。</param>
    private static bool TryResolveReference(
        SqlTableReference reference,
        IReadOnlyList<SqlToken> tokens,
        IReadOnlyDictionary<string, SqlCommonTableExpression> ctes,
        string? qualifier,
        int depth,
        HashSet<string> visiting,
        List<SqlWildcardColumnSource> sources)
    {
        if (depth > MaximumDepth)
        {
            return false;
        }

        if (reference.IsDerived)
        {
            var open = FindTokenAt(tokens, reference.Start);

            // 衍生資料表的第一個詞法單元是左括號；資料表變數（@t）不是，
            // 而它的欄位既不在指令碼裡也不在中繼資料裡，只能放棄。
            if (open < 0 || !tokens[open].IsPunctuation("("))
            {
                return false;
            }

            var close = FindClosingParenthesis(tokens, open);

            return close > open
                && TryExpandQuery(tokens, open + 1, close, ctes, qualifier, depth + 1, visiting, sources);
        }

        // CTE 名稱不帶結構描述；dbo.c 指的一定是資料庫裡的物件，不是 CTE。
        if (reference.SchemaName is null && ctes.TryGetValue(reference.ObjectName, out var cte))
        {
            if (cte.ColumnNames.Count > 0)
            {
                sources.Add(SqlWildcardColumnSource.FromNames(cte.ColumnNames, qualifier));
                return true;
            }

            // 遞迴 CTE 會參照自己，沒有這道關就會一直展開下去。
            if (!visiting.Add(cte.Name))
            {
                return false;
            }

            try
            {
                return TryExpandQuery(tokens, cte.BodyStart, cte.BodyEnd, ctes, qualifier, depth + 1, visiting, sources);
            }
            finally
            {
                visiting.Remove(cte.Name);
            }
        }

        sources.Add(SqlWildcardColumnSource.FromTable(reference, qualifier));
        return true;
    }

    /// <summary>讀出一段查詢的輸出欄位。</summary>
    private static bool TryExpandQuery(
        IReadOnlyList<SqlToken> tokens,
        int start,
        int end,
        IReadOnlyDictionary<string, SqlCommonTableExpression> ctes,
        string? qualifier,
        int depth,
        HashSet<string> visiting,
        List<SqlWildcardColumnSource> sources)
    {
        var index = start;

        // ((SELECT …)) 這種寫法的外層括號要看穿。
        while (index < end && tokens[index].IsPunctuation("("))
        {
            index++;
        }

        if (index >= end || !tokens[index].IsKeyword("SELECT"))
        {
            return false;
        }

        var selectIndex = index;
        index = SkipSelectListPrelude(tokens, index + 1, end);

        var names = new List<string>();
        IReadOnlyList<SqlTableReference>? innerSources = null;

        while (index < end)
        {
            var itemEnd = FindItemEnd(tokens, index, end);

            if (itemEnd == index)
            {
                break;
            }

            if (IsWildcardItem(tokens, index, itemEnd, out var itemQualifier))
            {
                // 名稱要先落地，否則 SELECT Id, * 展開後 Id 會排到資料表欄位後面。
                Flush(names, qualifier, sources);

                innerSources ??= SqlScopeAnalyzer.ExtractSources(tokens, selectIndex, end);

                if (!TryResolveInnerWildcard(
                        tokens, ctes, innerSources, itemQualifier, qualifier, depth, visiting, sources))
                {
                    return false;
                }
            }
            else if (TryGetOutputName(tokens, index, itemEnd, out var name))
            {
                names.Add(name);
            }
            else
            {
                // 沒有名稱的運算式（SELECT a + b）在外層就是「(無資料行名稱)」，
                // 展開成欄位清單時無從稱呼它。
                return false;
            }

            index = itemEnd;

            if (index < end && tokens[index].IsPunctuation(","))
            {
                index++;
                continue;
            }

            break;
        }

        Flush(names, qualifier, sources);
        return true;
    }

    private static bool TryResolveInnerWildcard(
        IReadOnlyList<SqlToken> tokens,
        IReadOnlyDictionary<string, SqlCommonTableExpression> ctes,
        IReadOnlyList<SqlTableReference> innerSources,
        string? itemQualifier,
        string? qualifier,
        int depth,
        HashSet<string> visiting,
        List<SqlWildcardColumnSource> sources)
    {
        if (innerSources.Count == 0)
        {
            return false;
        }

        if (itemQualifier is null)
        {
            foreach (var inner in innerSources)
            {
                if (!TryResolveReference(inner, tokens, ctes, qualifier, depth, visiting, sources))
                {
                    return false;
                }
            }

            return true;
        }

        var scope = new SqlStatementScope(innerSources, 0, 0);

        return scope.TryResolve(itemQualifier, out var target)
            && TryResolveReference(target, tokens, ctes, qualifier, depth, visiting, sources);
    }

    private static void Flush(List<string> names, string? qualifier, List<SqlWildcardColumnSource> sources)
    {
        if (names.Count == 0)
        {
            return;
        }

        sources.Add(SqlWildcardColumnSource.FromNames(names.ToArray(), qualifier));
        names.Clear();
    }

    private static int SkipSelectListPrelude(IReadOnlyList<SqlToken> tokens, int index, int end)
    {
        while (index < end)
        {
            var token = tokens[index];

            if (token.IsKeyword("TOP"))
            {
                index++;

                if (index < end && tokens[index].IsPunctuation("("))
                {
                    var close = FindClosingParenthesis(tokens, index);
                    index = close > index ? close + 1 : end;
                    continue;
                }

                if (index < end && tokens[index].Kind is SqlTokenKind.Number or SqlTokenKind.Variable)
                {
                    index++;
                }

                continue;
            }

            if (token.Kind == SqlTokenKind.Identifier && !token.IsQuoted && SelectListPrelude.Contains(token.Value))
            {
                index++;
                continue;
            }

            break;
        }

        return index;
    }

    /// <summary>選取清單裡下一個逗號或子句關鍵字的位置。</summary>
    private static int FindItemEnd(IReadOnlyList<SqlToken> tokens, int index, int end)
    {
        var depth = 0;

        for (var i = index; i < end; i++)
        {
            var token = tokens[i];

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

            if (depth > 0)
            {
                continue;
            }

            if (token.IsPunctuation(",") || token.IsPunctuation(";"))
            {
                return i;
            }

            if (token.Kind == SqlTokenKind.Identifier &&
                !token.IsQuoted &&
                SelectListEnd.Contains(token.Value))
            {
                return i;
            }
        }

        return end;
    }

    /// <summary>選取項是不是 <c>*</c> 或 <c>別名.*</c>。</summary>
    private static bool IsWildcardItem(
        IReadOnlyList<SqlToken> tokens,
        int start,
        int end,
        out string? qualifier)
    {
        qualifier = null;

        if (end - start == 1)
        {
            return tokens[start].Kind == SqlTokenKind.Operator && tokens[start].Value == "*";
        }

        if (end - start != 3 ||
            tokens[start].Kind != SqlTokenKind.Identifier ||
            !tokens[start + 1].IsPunctuation(".") ||
            tokens[start + 2].Kind != SqlTokenKind.Operator ||
            tokens[start + 2].Value != "*")
        {
            return false;
        }

        qualifier = tokens[start].Value;
        return true;
    }

    /// <summary>
    /// 一個選取項在外層看到的欄位名稱。
    /// </summary>
    /// <remarks>
    /// T-SQL 有三種命名寫法，順序不能顛倒：<c>AS 名稱</c>、<c>名稱 = 運算式</c>、
    /// 直接把名稱接在運算式後面。都沒有時才退回「這一項本身就是欄位參照」，
    /// 取它的最後一段。
    /// </remarks>
    private static bool TryGetOutputName(IReadOnlyList<SqlToken> tokens, int start, int end, out string name)
    {
        name = string.Empty;

        if (end <= start)
        {
            return false;
        }

        var last = tokens[end - 1];

        // expr AS 名稱
        if (end - start >= 3 && tokens[end - 2].IsKeyword("AS") && last.Kind == SqlTokenKind.Identifier)
        {
            name = last.Value;
            return true;
        }

        // 名稱 = expr
        if (end - start >= 3 &&
            tokens[start].Kind == SqlTokenKind.Identifier &&
            tokens[start + 1].Kind == SqlTokenKind.Operator &&
            tokens[start + 1].Value == "=")
        {
            name = tokens[start].Value;
            return true;
        }

        // expr 名稱（省略 AS）。前一個詞法單元決定最後那個識別字是別名還是
        // 運算式的一部分：ISNULL(a, 0) x 是別名，a.b 的 b 不是。
        if (end - start >= 2 && last.Kind == SqlTokenKind.Identifier && IsAliasFollower(tokens[end - 2]))
        {
            if (!last.IsQuoted && SelectListTerminators.Contains(last.Value))
            {
                return false;
            }

            name = last.Value;
            return true;
        }

        // 單純的欄位參照：Id、a.Id、dbo.t.Id
        if (last.Kind != SqlTokenKind.Identifier)
        {
            return false;
        }

        for (var i = start; i < end - 1; i++)
        {
            if (tokens[i].Kind != SqlTokenKind.Identifier && !tokens[i].IsPunctuation("."))
            {
                return false;
            }
        }

        name = last.Value;
        return true;
    }

    /// <summary>前面接著這種詞法單元的識別字，是省略了 <c>AS</c> 的別名。</summary>
    private static bool IsAliasFollower(SqlToken token)
    {
        return token.Kind is SqlTokenKind.Number or SqlTokenKind.String or SqlTokenKind.Variable
            || token.IsPunctuation(")")
            || (token.Kind == SqlTokenKind.Identifier && token.IsQuoted);
    }

    /// <summary>
    /// 收集指令碼裡所有的 CTE。
    /// </summary>
    /// <remarks>
    /// 不限定在游標所在的批次裡找：CTE 名稱在一份指令碼裡幾乎不會重複，
    /// 而要正確劃出批次邊界得再維護一套規則，代價高於它擋掉的問題。
    ///
    /// <c>WITH (NOLOCK)</c> 這類資料表提示自然被排除——它後面接的是左括號
    /// 而不是名稱。
    /// </remarks>
    private static IReadOnlyDictionary<string, SqlCommonTableExpression> CollectCommonTableExpressions(
        IReadOnlyList<SqlToken> tokens)
    {
        var result = new Dictionary<string, SqlCommonTableExpression>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < tokens.Count; index++)
        {
            if (!tokens[index].IsKeyword("WITH"))
            {
                continue;
            }

            var cursor = index + 1;

            while (cursor < tokens.Count)
            {
                var name = tokens[cursor];

                if (name.Kind != SqlTokenKind.Identifier ||
                    (!name.IsQuoted && SelectListTerminators.Contains(name.Value)))
                {
                    break;
                }

                cursor++;
                var columns = Array.Empty<string>() as IReadOnlyList<string>;

                if (cursor < tokens.Count && tokens[cursor].IsPunctuation("("))
                {
                    var listEnd = FindClosingParenthesis(tokens, cursor);

                    if (listEnd < 0)
                    {
                        break;
                    }

                    columns = ReadColumnList(tokens, cursor + 1, listEnd);
                    cursor = listEnd + 1;
                }

                if (cursor + 1 >= tokens.Count ||
                    !tokens[cursor].IsKeyword("AS") ||
                    !tokens[cursor + 1].IsPunctuation("("))
                {
                    break;
                }

                var bodyEnd = FindClosingParenthesis(tokens, cursor + 1);

                if (bodyEnd < 0)
                {
                    break;
                }

                // 同名時保留先出現的那一個，與 T-SQL 不允許重複命名的前提一致。
                if (!result.ContainsKey(name.Value))
                {
                    result.Add(
                        name.Value,
                        new SqlCommonTableExpression(name.Value, columns, cursor + 2, bodyEnd));
                }

                cursor = bodyEnd + 1;

                if (cursor < tokens.Count && tokens[cursor].IsPunctuation(","))
                {
                    cursor++;
                    continue;
                }

                break;
            }

            index = Math.Max(index, cursor - 1);
        }

        return result;
    }

    private static IReadOnlyList<string> ReadColumnList(IReadOnlyList<SqlToken> tokens, int start, int end)
    {
        var names = new List<string>();

        for (var index = start; index < end; index++)
        {
            if (tokens[index].Kind == SqlTokenKind.Identifier)
            {
                names.Add(tokens[index].Value);
            }
        }

        return names;
    }

    private static int FindTokenAt(IReadOnlyList<SqlToken> tokens, int position)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Start == position)
            {
                return index;
            }

            if (tokens[index].Start > position)
            {
                break;
            }
        }

        return -1;
    }

    private static int FindClosingParenthesis(IReadOnlyList<SqlToken> tokens, int open)
    {
        var depth = 0;

        for (var index = open; index < tokens.Count; index++)
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

    private static int FindOpeningParenthesis(IReadOnlyList<SqlToken> tokens, int close)
    {
        var depth = 0;

        for (var index = close; index >= 0; index--)
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
}
