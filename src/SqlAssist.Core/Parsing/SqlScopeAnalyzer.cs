using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 從詞法串流找出游標所在的查詢範圍，以及該範圍看得到的資料來源。
/// </summary>
/// <remarks>
/// 這是別名解析的基礎：<c>FROM dbo.Lib_Reader u</c> 之後輸入 <c>u.</c> 時，
/// 要知道 <c>u</c> 指向哪一張資料表才能列出欄位。
///
/// 範圍以括號界定，但**只有開啟查詢的括號算數**：子查詢內的游標看到的是
/// 子查詢自己的 FROM 子句，而 <c>COUNT(…)</c>、<c>ISNULL(…)</c>、
/// <c>WHERE (…)</c>、<c>IN (…)</c> 這些只是運算式的一部分，
/// 裡面仍然看得見外層的 FROM 子句。
/// </remarks>
public static class SqlScopeAnalyzer
{
    /// <summary>可以獨立成為一個敘述開頭的關鍵字。</summary>
    /// <remarks>
    /// 刻意不含 <c>SET</c> 與 <c>WITH</c>：
    /// <c>SET</c> 會把 <c>UPDATE u SET … FROM …</c> 從中間切斷，
    /// <c>WITH</c> 則同時是 CTE 開頭與資料表提示（<c>WITH (NOLOCK)</c>）。
    /// 少判一個邊界只會讓範圍偏大，多判一個會讓 FROM 子句整個消失。
    /// </remarks>
    private static readonly HashSet<string> StatementKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE",
            "CREATE", "ALTER", "DROP", "EXEC", "EXECUTE", "DECLARE",
            "IF", "WHILE", "RETURN", "PRINT", "USE", "GRANT", "REVOKE", "DENY"
        };

    /// <summary>不可能是資料表名稱或別名的關鍵字。</summary>
    private static readonly HashSet<string> ClauseKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "WHERE", "GROUP", "ORDER", "HAVING", "BY", "JOIN", "INNER", "LEFT",
            "RIGHT", "FULL", "CROSS", "OUTER", "APPLY", "ON", "UNION", "EXCEPT",
            "INTERSECT", "SET", "VALUES", "OPTION", "FOR", "PIVOT", "UNPIVOT",
            "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "WHEN", "THEN",
            "USING", "AND", "OR", "NOT", "TOP", "DISTINCT", "INTO", "EXEC",
            "EXECUTE", "DECLARE", "IF", "WHILE", "BEGIN", "END", "ELSE",
            "RETURN", "OUTPUT", "GO", "AS", "WITH", "TABLESAMPLE", "ASC",
            "DESC", "PERCENT", "TIES", "FROM", "TABLE", "CASE", "ELSE", "NULL"
        };

    /// <summary>會在後面接資料來源的關鍵字。</summary>
    private static readonly HashSet<string> SourceKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "JOIN", "APPLY", "INTO", "UPDATE", "USING"
        };

    /// <summary>
    /// 分析游標所在的查詢範圍。
    /// </summary>
    /// <remarks>
    /// 會對整份文字做詞法分析。判斷游標是否位於字串或註解內本來就需要從頭掃描，
    /// 因此成本與既有的語彙狀態判斷同級，不是新增的負擔。
    /// </remarks>
    public static SqlStatementScope Analyze(string sql, int caretPosition)
    {
        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        if (caretPosition < 0 || caretPosition > sql.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(caretPosition));
        }

        return Analyze(SqlTokenizer.Tokenize(sql), caretPosition);
    }

    /// <summary>
    /// 以既有的詞法串流分析範圍。
    /// </summary>
    /// <remarks>
    /// 呼叫端手上已經有詞法串流時走這裡，省下第二次全文掃描——
    /// 萬用字元展開就是這種情形：它得先自己看過詞法單元才知道有沒有事要做。
    /// </remarks>
    public static SqlStatementScope Analyze(IReadOnlyList<SqlToken> tokens, int caretPosition)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if (tokens.Count == 0)
        {
            return SqlStatementScope.Empty;
        }

        var caretIndex = FindCaretTokenIndex(tokens, caretPosition);
        var start = FindScopeStart(tokens, caretIndex);

        // 範圍起點可能落在最後一個詞法單元之後，例如剛輸入 "FROM (" 的當下。
        if (start >= tokens.Count)
        {
            return new SqlStatementScope(Array.Empty<SqlTableReference>(), caretPosition, caretPosition);
        }

        var end = FindScopeEnd(tokens, start);
        var tables = ExtractSources(tokens, start, end);

        return new SqlStatementScope(
            tables,
            tokens[start].Start,
            end > start ? tokens[end - 1].End : tokens[start].Start);
    }

    /// <summary>最後一個起點在游標之前的詞法單元。</summary>
    private static int FindCaretTokenIndex(IReadOnlyList<SqlToken> tokens, int caretPosition)
    {
        var index = -1;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Start >= caretPosition)
            {
                break;
            }

            index = i;
        }

        return index < 0 ? 0 : index;
    }

    private static int FindScopeStart(IReadOnlyList<SqlToken> tokens, int caretIndex)
    {
        var depth = 0;

        for (var i = caretIndex; i >= 0; i--)
        {
            var token = tokens[i];

            if (token.IsPunctuation(")"))
            {
                depth++;
                continue;
            }

            if (token.IsPunctuation("("))
            {
                if (depth == 0)
                {
                    // 深度已經是 0 卻遇到左括號，代表游標在這個括號內。
                    // 但括號不一定開啟新的查詢：把 COUNT( 也當成子查詢的話，
                    // SELECT COUNT(a.| FROM T a 的範圍就只剩括號裡那一段，
                    // 別名 a 永遠解析不出來——那正是彙總函式裡沒有欄位建議的原因。
                    if (SqlTokenNavigator.OpensQuery(tokens, i))
                    {
                        return i + 1;
                    }

                    // 只是運算式的括號，對範圍而言不存在，繼續往外找。
                    continue;
                }

                depth--;
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            if (token.IsPunctuation(";") || token.IsKeyword("GO"))
            {
                return i + 1;
            }

            if (token.Kind == SqlTokenKind.Identifier &&
                !token.IsQuoted &&
                StatementKeywords.Contains(token.Value) &&
                !IsMergeAction(tokens, i))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// 這個敘述關鍵字其實是 MERGE 的動作子句，不是新敘述的開頭。
    /// </summary>
    /// <remarks>
    /// <c>WHEN MATCHED THEN UPDATE SET …</c>、<c>WHEN NOT MATCHED THEN INSERT …</c>
    /// 裡的三個關鍵字屬於同一個 MERGE。把它們當成邊界的話，游標一進到 <c>WHEN</c>
    /// 之後，<c>target</c> 與 <c>source</c> 兩個別名就全部解析不出來——症狀是
    /// <c>target.|</c> 與 <c>source.|</c> 都不再列欄位，而 <c>INSERT (|)</c> 連
    /// 一個候選都沒有。
    ///
    /// 認的是<b>前一個詞元是不是 THEN</b>，不是「這份指令碼裡有沒有 MERGE」。
    /// 一個 MERGE 之後接著獨立的 UPDATE，那個 UPDATE 仍然必須切斷範圍。
    /// T-SQL 裡 THEN 只出現在 CASE 與 MERGE，而 CASE 的 THEN 後面是運算式，
    /// 不會是這三個關鍵字。
    /// </remarks>
    private static bool IsMergeAction(IReadOnlyList<SqlToken> tokens, int index)
    {
        if (index <= 0 || !tokens[index - 1].IsKeyword("THEN"))
        {
            return false;
        }

        var token = tokens[index];

        return token.IsKeyword("UPDATE") ||
               token.IsKeyword("INSERT") ||
               token.IsKeyword("DELETE");
    }

    private static int FindScopeEnd(IReadOnlyList<SqlToken> tokens, int start)
    {
        var depth = 0;

        for (var i = start; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (token.IsPunctuation("("))
            {
                depth++;
                continue;
            }

            if (token.IsPunctuation(")"))
            {
                if (depth == 0)
                {
                    return i;
                }

                depth--;
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            if (token.IsPunctuation(";") || token.IsKeyword("GO"))
            {
                return i;
            }

            if (i > start &&
                token.Kind == SqlTokenKind.Identifier &&
                !token.IsQuoted &&
                StatementKeywords.Contains(token.Value) &&
                !IsMergeAction(tokens, i))
            {
                return i;
            }
        }

        return tokens.Count;
    }

    /// <summary>
    /// 讀出 <paramref name="start"/> 到 <paramref name="end"/> 這段查詢的資料來源。
    /// </summary>
    /// <remarks>
    /// 只認<b>深度 0</b> 的 <c>FROM</c>／<c>JOIN</c>：巢狀子查詢的 FROM 子句屬於它自己，
    /// <c>SELECT * FROM T WHERE x IN (SELECT y FROM Z)</c> 的外層看不到 <c>Z</c>。
    /// 資料來源本身的括號（衍生資料表、資料表值函式、資料表提示）由
    /// <see cref="TryParseTableReference"/> 一次跳完，跳過的那一段括號是配對的，
    /// 因此不影響深度。
    ///
    /// 深度<b>只算配對得起來的括號</b>。編輯中的敘述幾乎總是有一個還沒關上的括號，
    /// 而那個括號後面往往正是使用者要的東西：<c>SELECT COUNT(a.| FROM dbo.PUBLISHER a</c>
    /// 的左括號永遠等不到右括號，把它算進深度就會讓整個 FROM 子句消失，
    /// 別名 <c>a</c> 也就永遠解析不出來。
    /// </remarks>
    public static IReadOnlyList<SqlTableReference> ExtractSources(
        IReadOnlyList<SqlToken> tokens,
        int start,
        int end)
    {
        var references = new List<SqlTableReference>();
        var paired = SqlTokenNavigator.FindPairedParentheses(tokens, start, end);
        var index = start;
        var depth = 0;

        while (index < end)
        {
            var token = tokens[index];

            if (paired[index - start])
            {
                depth += token.IsPunctuation("(") ? 1 : -1;
                index++;
                continue;
            }

            if (depth > 0 ||
                token.Kind != SqlTokenKind.Identifier ||
                token.IsQuoted ||
                !SourceKeywords.Contains(token.Value))
            {
                index++;
                continue;
            }

            // FROM 與 INTO 後面可以是逗號分隔的清單，JOIN／APPLY／USING 只接一個。
            var allowsList = token.IsKeyword("FROM") || token.IsKeyword("INTO");
            index++;

            while (index < end)
            {
                if (!TryParseTableReference(tokens, index, end, out var reference, out var next))
                {
                    break;
                }

                references.Add(reference);
                index = next;

                if (!allowsList || index >= end || !tokens[index].IsPunctuation(","))
                {
                    break;
                }

                index++;
            }
        }

        return references;
    }

    private static bool TryParseTableReference(
        IReadOnlyList<SqlToken> tokens,
        int index,
        int end,
        out SqlTableReference reference,
        out int next)
    {
        reference = null!;
        next = index;

        if (index >= end)
        {
            return false;
        }

        var start = index;
        var first = tokens[index];
        string? schemaName = null;
        var objectName = string.Empty;
        var isDerived = false;

        if (first.IsPunctuation("("))
        {
            // 衍生資料表或資料表值建構式：查不到中繼資料，但別名仍要記下來，
            // 否則後面用這個別名限定欄位時會誤判成資料表名稱。
            index = SqlTokenNavigator.SkipParenthesised(tokens, index, end);
            isDerived = true;
        }
        else if (first.Kind == SqlTokenKind.Variable)
        {
            objectName = first.Value;
            isDerived = true;
            index++;
        }
        else if (first.Kind == SqlTokenKind.Identifier && (first.IsQuoted || !ClauseKeywords.Contains(first.Value)))
        {
            var parts = new List<string>();

            while (index < end &&
                   tokens[index].Kind == SqlTokenKind.Identifier &&
                   (tokens[index].IsQuoted || !ClauseKeywords.Contains(tokens[index].Value)))
            {
                parts.Add(tokens[index].Value);
                index++;

                if (index < end && tokens[index].IsPunctuation("."))
                {
                    index++;

                    // db..object 這種寫法中間那段是空的。
                    if (index < end && tokens[index].IsPunctuation("."))
                    {
                        parts.Add(string.Empty);
                    }

                    continue;
                }

                break;
            }

            if (parts.Count == 0)
            {
                return false;
            }

            objectName = parts[parts.Count - 1];
            schemaName = parts.Count >= 2 ? parts[parts.Count - 2] : null;

            if (string.IsNullOrEmpty(schemaName))
            {
                schemaName = null;
            }

            // 資料表值函式：CROSS APPLY dbo.fn_Split(x) s
            if (index < end && tokens[index].IsPunctuation("("))
            {
                index = SqlTokenNavigator.SkipParenthesised(tokens, index, end);
            }
        }
        else
        {
            return false;
        }

        // 資料表提示夾在名稱與別名之間：FROM Loans WITH (NOLOCK) o
        if (index + 1 < end && tokens[index].IsKeyword("WITH") && tokens[index + 1].IsPunctuation("("))
        {
            index = SqlTokenNavigator.SkipParenthesised(tokens, index + 1, end);
        }

        var alias = TryReadAlias(tokens, ref index, end);

        reference = new SqlTableReference(
            schemaName,
            objectName,
            alias,
            isDerived,
            tokens[start].Start,
            tokens[Math.Max(start, index - 1)].End);

        next = index;
        return true;
    }

    private static string? TryReadAlias(IReadOnlyList<SqlToken> tokens, ref int index, int end)
    {
        var cursor = index;

        if (cursor < end && tokens[cursor].IsKeyword("AS"))
        {
            cursor++;
        }

        if (cursor >= end)
        {
            return null;
        }

        var candidate = tokens[cursor];

        if (candidate.Kind != SqlTokenKind.Identifier)
        {
            return null;
        }

        if (!candidate.IsQuoted && ClauseKeywords.Contains(candidate.Value))
        {
            return null;
        }

        index = cursor + 1;
        return candidate.Value;
    }
}
