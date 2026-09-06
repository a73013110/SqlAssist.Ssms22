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

    /// <summary>別名後面接得住資料行清單的資料列集函式。</summary>
    /// <remarks>
    /// T-SQL 的 <c>table_source</c> 文法裡 <c>rowset_function</c> 與
    /// <c>derived_table</c> 一樣有 <c>(column_alias …)</c>，<c>user_defined_function</c>
    /// 沒有——同樣是「名稱加引數清單」的形狀，能不能接資料行清單卻不同，所以這三個
    /// 名字只能寫死。它們是文法的一部分而不是使用者物件，帶結構描述的
    /// <c>dbo.OPENROWSET(…)</c> 因此不在此列。
    ///
    /// <c>OPENXML</c> 不收：它在文法裡自成一條，後面接的是 <c>WITH (結構描述)</c>
    /// 而不是資料行清單。
    /// </remarks>
    private static readonly HashSet<string> RowsetFunctions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "OPENROWSET", "OPENQUERY", "OPENDATASOURCE"
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

        var end = FindStatementEnd(tokens, start);
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

    /// <summary>
    /// 從 <paramref name="start"/> 這個詞法單元起算，這一句敘述到哪裡結束（不含）。
    /// </summary>
    /// <remarks>
    /// 深度 0 的分號、<c>GO</c>、右括號，或下一個敘述開頭的關鍵字；都沒有就到文字結尾。
    ///
    /// 公開出來是因為問這個問題的不只範圍分析：<c>SELECT … INTO #tmp</c> 的名冊要
    /// 知道那句 <c>SELECT</c> 涵蓋到哪裡，才讀得出它投影出來的資料行。各寫一份的
    /// 症狀是同一段文字在兩處切在不同的地方，而偏掉的那一份沒有任何徵兆——
    /// 只是資料來源清單多出或少掉幾張表。
    /// </remarks>
    public static int FindStatementEnd(IReadOnlyList<SqlToken> tokens, int start)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

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

        // SELECT … INTO #tmp 的 INTO 接的是一張正要建立的資料表，不是這句查詢讀得到
        // 的來源。INSERT INTO 的那一個相反——它就是使用者要填資料行的目標，而兩者的
        // 形狀一模一樣，分辨的憑據只有這句敘述的第一個字。收錯的症狀是 WHERE | 把
        // 那張表投影出來的欄位跟真正的來源混在一起列出來。
        var selectInto = start < end && tokens[start].IsKeyword("SELECT");

        while (index < end)
        {
            var token = tokens[index];

            if (paired[index - start])
            {
                depth += token.IsPunctuation("(") ? 1 : -1;
                index++;
                continue;
            }

            // ON 不在 SourceKeywords 裡，它絕大多數時候是 JOIN 條件；只有
            // CREATE INDEX ix ON t、CREATE TRIGGER tr ON t 這一族後面接的是資料表。
            // 少了這一條，索引與觸發程序的 DDL 裡完全沒有欄位建議，
            // 而清單會退化成整個資料庫的物件——見 SqlDdlTarget。
            if (depth > 0 ||
                token.Kind != SqlTokenKind.Identifier ||
                token.IsQuoted ||
                (!SourceKeywords.Contains(token.Value) &&
                    !SqlDdlTarget.IsDataSourceOn(tokens, index)))
            {
                index++;
                continue;
            }

            // FROM 與 INTO 後面可以是逗號分隔的清單，JOIN／APPLY／USING 只接一個。
            var allowsList = token.IsKeyword("FROM") || token.IsKeyword("INTO");
            var collects = !selectInto || !token.IsKeyword("INTO");
            index++;

            while (index < end)
            {
                if (!TryParseTableReference(tokens, index, end, out var reference, out var next))
                {
                    break;
                }

                if (collects)
                {
                    references.Add(reference);
                }

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
        SqlObjectPath? path = null;
        var derivedName = string.Empty;
        var isDerived = false;

        // 別名後面的資料行清單只接在衍生資料表與資料列集函式後面，見 TryReadColumnList。
        var takesColumnList = false;

        if (first.IsPunctuation("("))
        {
            // 衍生資料表或資料表值建構式：查不到中繼資料，但別名仍要記下來，
            // 否則後面用這個別名限定欄位時會誤判成資料表名稱。
            index = SqlTokenNavigator.SkipParenthesised(tokens, index, end);
            isDerived = true;
            takesColumnList = true;
        }
        else if (first.Kind == SqlTokenKind.Variable)
        {
            derivedName = first.Value;
            isDerived = true;
            index++;
        }
        else if (first.Kind == SqlTokenKind.Identifier && (first.IsQuoted || !ClauseKeywords.Contains(first.Value)))
        {
            var parts = new List<string>();

            while (index < end)
            {
                if (tokens[index].Kind == SqlTokenKind.Identifier &&
                    (tokens[index].IsQuoted || !ClauseKeywords.Contains(tokens[index].Value)))
                {
                    parts.Add(tokens[index].Value);
                    index++;
                }
                else if (parts.Count > 0 && tokens[index].IsPunctuation("."))
                {
                    // 剛吃掉一個點號又碰到一個，代表中間這一段省略了：
                    // db..object 少寫結構描述，server...object 連資料庫也少寫。
                    // 補一個空段而不是跳過，位置才對得回去——跳過的話
                    // server...object 會右對齊成 server.object，指到別的東西。
                    parts.Add(string.Empty);
                }
                else
                {
                    break;
                }

                if (index < end && tokens[index].IsPunctuation("."))
                {
                    index++;
                    continue;
                }

                break;
            }

            if (parts.Count == 0)
            {
                return false;
            }

            // 段數超過上限的名稱查不到，但別名仍要記下來，所以退成衍生來源
            // 而不是整個丟掉：丟掉的話後面用這個別名限定欄位會被誤判成結構描述。
            if (!SqlObjectPath.TryParseName(parts, out path))
            {
                derivedName = parts[parts.Count - 1];
                isDerived = true;
            }

            // 資料表值函式：CROSS APPLY dbo.fn_Split(x) s
            if (index < end && tokens[index].IsPunctuation("("))
            {
                index = SqlTokenNavigator.SkipParenthesised(tokens, index, end);

                // 引數清單跳完的形狀相同，但只有資料列集函式接得住資料行清單。
                takesColumnList = parts.Count == 1 &&
                    !first.IsQuoted &&
                    RowsetFunctions.Contains(parts[0]);
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
        var columnNames = TryReadColumnList(tokens, ref index, end, takesColumnList && alias is not null);

        var referenceStart = tokens[start].Start;
        var referenceEnd = tokens[Math.Max(start, index - 1)].End;

        reference = isDerived || path is null
            ? new SqlTableReference(derivedName, alias, referenceStart, referenceEnd, columnNames)
            : new SqlTableReference(path, alias, referenceStart, referenceEnd, columnNames);

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

    /// <summary>
    /// 別名後面明確寫出的資料行清單：<c>(VALUES (1, N'Alice')) AS T (ID, Name)</c>。
    /// </summary>
    /// <remarks>
    /// 接得住它的只有<b>衍生資料表</b>與 <see cref="RowsetFunctions"/>——T-SQL 的
    /// <c>table_source</c> 文法裡只有這兩條後面有 <c>(column_alias …)</c>，具名資料表
    /// 與使用者定義的資料表值函式後面就只有別名。所以別名後面那串括號是不是資料行
    /// 清單，由<b>來源的形狀</b>決定，不去猜括號裡寫了什麼。
    ///
    /// 猜括號內容行不通：<c>FROM dbo.Loan l (NOLOCK)</c> 與
    /// <c>FROM dbo.fn_Loans(0) f (NOLOCK)</c> 都是舊式資料表提示，形狀與資料行清單
    /// 一模一樣。實測回報過的症狀是 <c>SELECT * INTO #Temp FROM dbo.fn(x) f (NOLOCK)</c>
    /// 之後，<c>#Temp</c> 的結構只剩一個叫 NOLOCK 的欄位——而假結構會一路傳到預覽與
    /// <c>INSERT INTO #Temp</c> 的整句展開。用關鍵字名單分辨也不對：<c>NOLOCK</c>
    /// 不是保留字，資料行真的叫得出這個名字。
    ///
    /// 走到這裡不必再分辨資料表提示與函式引數：<c>WITH (NOLOCK)</c> 與函式自己的
    /// 引數清單都在別名<b>之前</b>就跳完了。沒有別名也不收，文法要求這串括號接在
    /// 別名後面。
    ///
    /// 括號還沒關上時當成沒寫，並把位置留在原地：使用者正打到一半，而讀一半的清單
    /// 會覆寫掉主體算得出來的名稱。
    /// </remarks>
    private static IReadOnlyList<string> TryReadColumnList(
        IReadOnlyList<SqlToken> tokens,
        ref int index,
        int end,
        bool takesColumnList)
    {
        if (!takesColumnList || index >= end || !tokens[index].IsPunctuation("("))
        {
            return Array.Empty<string>();
        }

        var close = SqlTokenNavigator.FindClosingParenthesis(tokens, index, end);

        if (close < 0)
        {
            return Array.Empty<string>();
        }

        var names = ReadColumnList(tokens, index + 1, close);
        index = close + 1;
        return names;
    }

    /// <summary>讀出一對括號之間的資料行名稱。</summary>
    /// <remarks>
    /// CTE 的 <c>WITH c (a, b)</c> 與資料來源的 <c>AS T (a, b)</c> 是同一串東西，
    /// 因此只有這一份實作；兩處各寫一份的話，其中一邊多認得一種寫法就會對同一段
    /// 文字給出不同的欄位。
    /// </remarks>
    internal static IReadOnlyList<string> ReadColumnList(
        IReadOnlyList<SqlToken> tokens,
        int start,
        int end)
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
}
