using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;

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
///
/// 子查詢自己的來源之外，外層查詢的來源掛在 <see cref="SqlStatementScope.Outer"/>：
/// 相互關聯子查詢引用的正是它們。
/// </remarks>
public static class SqlScopeAnalyzer
{
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

    /// <summary>別名後面只接資料行清單的資料列集函式。</summary>
    /// <remarks>
    /// 同樣是「名稱加引數清單」的形狀，資料列集函式與衍生資料表一樣後面只有
    /// <c>(column_alias …)</c>，使用者定義的函式卻還寫得出舊式資料表提示，所以這三個
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
    /// <remarks>
    /// MERGE、UPDATE 與 DELETE 同理，後面直接是目標，中間可以夾 TOP 子句（<c>MERGE TOP (10) dbo.Loan t USING …</c>、省略 FROM 的
    /// <c>DELETE dbo.Loan WHERE …</c>）；寫了 INTO、FROM 的由它們收。目標是之後 FROM 取的別名時
    /// （<c>DELETE l FROM dbo.Loan l</c>）與 UPDATE 一樣由 <see cref="RemoveAliasReferences"/> 拿掉。
    /// 動詞要是一句的開頭（<see cref="SqlStatementBoundaries.FindDmlTarget"/>）：聯結提示 <c>INNER MERGE JOIN</c>、MERGE 動作的
    /// <c>THEN DELETE</c> 與游標查詢 <c>FOR UPDATE OF CopyNo</c> 的 UPDATE 都不是動詞，後面不收來源——OF 不是一張表。
    /// </remarks>
    private static readonly HashSet<string> SourceKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "JOIN", "APPLY", "INTO", "UPDATE", "DELETE", "MERGE", "USING"
        };

    /// <summary><see cref="SourceKeywords"/> 裡的 DML 動詞：後面是目標，只在一句的開頭才是。</summary>
    private static bool IsDmlVerb(SqlToken token) =>
        token.IsKeyword("UPDATE") || token.IsKeyword("DELETE") || token.IsKeyword("MERGE");

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

        return Analyze(sql, SqlTokenizer.Tokenize(sql), caretPosition);
    }

    /// <summary>
    /// 寫在 <paramref name="start"/> 的 <paramref name="name"/> 是不是某個資料來源的別名或公開名稱——
    /// 是的話，它後面接的那一段是欄位，不是同一個物件名稱的下一段。
    /// </summary>
    /// <remarks>
    /// 給 <see cref="SqlIdentifierScanner.FindNameAt"/> 回答它唯一要看範圍的那個問題；
    /// 資料來源自己的名稱不算（見 <see cref="SqlStatementScope.TryResolve(string, int, out SqlTableReference)"/>）。
    /// </remarks>
    public static bool NamesColumnOwner(string sql, int start, string name) =>
        Analyze(sql, start).TryResolve(name, start, out _);

    /// <summary>
    /// 以既有的詞法串流分析範圍。
    /// </summary>
    /// <remarks>
    /// 呼叫端手上已經有詞法串流時走這裡，省下第二次全文掃描——
    /// 萬用字元展開就是這種情形：它得先自己看過詞法單元才知道有沒有事要做。
    /// <paramref name="sql"/> 仍然要傳：語句的界線要看換行，換行只有原文有。
    /// </remarks>
    public static SqlStatementScope Analyze(string sql, IReadOnlyList<SqlToken> tokens, int caretPosition)
    {
        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if (caretPosition < 0 || caretPosition > sql.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(caretPosition));
        }

        if (tokens.Count == 0)
        {
            return SqlStatementScope.Empty;
        }

        var boundaries = new SqlStatementBoundaries(sql, tokens, caretPosition);
        var caret = FindCaretTokenIndex(tokens, caretPosition);
        var triggerTable = SqlChangeTables.FindTriggerTable(tokens, caret);
        var scope = AnalyzeAt(boundaries, caret, caretPosition, triggerTable);

        // OUTPUT 子句在觸發程序裡時看最內層：那句 DML 的目標。
        var owner = SqlChangeTables.FindOutputTarget(boundaries, caret) is { } target
            ? ResolveTarget(target, scope)
            : triggerTable;

        if (owner is null)
        {
            return scope;
        }

        var changeTables = new SqlTableReference[SqlChangeTables.Names.Count];

        for (var index = 0; index < changeTables.Length; index++)
        {
            changeTables[index] = Rename(owner, SqlChangeTables.Names[index], owner.Start, owner.End);
        }

        return new SqlStatementScope(scope.Tables, scope.Start, scope.End, scope.Outer, changeTables, triggerTable, scope.Lateral);
    }

    /// <summary>DML 的目標寫成別名時（<c>UPDATE l SET … OUTPUT … FROM dbo.Loan l</c>），換成別名指的來源。</summary>
    private static SqlTableReference ResolveTarget(SqlTableReference target, SqlStatementScope scope) =>
        target.Path is { SchemaName: null, DatabaseName: null, ServerName: null } &&
        string.IsNullOrEmpty(target.Alias) &&
        scope.TryResolve(target.ObjectName, out var aliased)
            ? aliased
            : target;

    /// <summary><paramref name="last"/> 這個詞元所在的範圍，連同包住它的外層。</summary>
    /// <param name="triggerTable">游標在 DML 觸發程序裡時的父資料表：FROM 寫的 inserted、deleted 指它。</param>
    private static SqlStatementScope AnalyzeAt(
        SqlStatementBoundaries boundaries,
        int last,
        int caretPosition,
        SqlTableReference? triggerTable)
    {
        var tokens = boundaries.Tokens;
        var start = FindScopeStart(boundaries, last);
        var outer = AnalyzeEnclosing(boundaries, start, triggerTable);

        // 範圍起點可能落在最後一個詞法單元之後，例如剛輸入 "FROM (" 的當下。
        if (start >= tokens.Count)
        {
            return new SqlStatementScope(Array.Empty<SqlTableReference>(), caretPosition, caretPosition, outer);
        }

        var end = FindStatementEnd(boundaries, start);
        var tables = ExtractSources(boundaries, start, end);

        if (triggerTable is not null)
        {
            tables = RenameChangeTables(tables, triggerTable);
        }

        return new SqlStatementScope(
            tables,
            tokens[start].Start,
            end > start ? tokens[end - 1].End : tokens[start].Start,
            outer,
            lateral: FindLateral(tokens, start, outer));
    }

    /// <summary>
    /// 從 <paramref name="start"/> 開始的範圍是 APPLY 右邊的衍生資料表時，外層寫在 APPLY 左邊的來源。
    /// </summary>
    /// <remarks>
    /// 外層的來源也包含右邊這個衍生資料表自己（它的別名寫在右括號之後），以位置排除：只收在左括號之前結束的。
    /// </remarks>
    private static IReadOnlyList<SqlTableReference>? FindLateral(
        IReadOnlyList<SqlToken> tokens,
        int start,
        SqlStatementScope? outer)
    {
        var open = start - 1;

        if (outer is null || open < 1 || !tokens[open].IsPunctuation("(") || !tokens[open - 1].IsKeyword("APPLY"))
        {
            return null;
        }

        var left = new List<SqlTableReference>();

        foreach (var table in outer.Tables)
        {
            if (table.End <= tokens[open].Start)
            {
                left.Add(table);
            }
        }

        return left;
    }

    /// <summary>
    /// FROM 寫的 <c>inserted</c>／<c>deleted</c> 換成觸發程序的父資料表，限定字照舊。
    /// </summary>
    /// <remarks>
    /// 中繼資料裡沒有叫這兩個名字的表：不換的話 <c>i.</c> 查一張不存在的表，沒寫限定字的欄位也少一份。
    /// 沒取別名的以名字當別名，<c>inserted.</c> 才解析得回來，別名清單也列得出它。
    /// </remarks>
    private static IReadOnlyList<SqlTableReference> RenameChangeTables(
        IReadOnlyList<SqlTableReference> tables,
        SqlTableReference triggerTable)
    {
        List<SqlTableReference>? renamed = null;

        for (var index = 0; index < tables.Count; index++)
        {
            var table = tables[index];

            if (!SqlChangeTables.IsChangeTable(table))
            {
                continue;
            }

            renamed ??= new List<SqlTableReference>(tables);
            renamed[index] = Rename(triggerTable, table.Alias ?? table.ObjectName, table.Start, table.End);
        }

        return (IReadOnlyList<SqlTableReference>?)renamed ?? tables;
    }

    /// <summary><paramref name="table"/> 這張表，以 <paramref name="alias"/> 限定。</summary>
    private static SqlTableReference Rename(SqlTableReference table, string alias, int start, int end) =>
        table.Path is { } path
            ? new SqlTableReference(path, alias, start, end)
            : new SqlTableReference(table.ObjectName, alias, start, end, table.ColumnNames);

    /// <summary>
    /// 從 <paramref name="start"/> 開始的範圍是子查詢時，括號外面那一層。
    /// </summary>
    /// <remarks>
    /// 範圍起點緊接在左括號後面，只會是 <see cref="FindScopeStart"/> 認定開啟查詢的那一個——
    /// 分號與 GO 之後的起點前面不是左括號。外層從括號前一個詞元再找一次，
    /// 每一層都比上一層短，遞迴深度就是子查詢的巢狀層數。
    /// </remarks>
    private static SqlStatementScope? AnalyzeEnclosing(
        SqlStatementBoundaries boundaries,
        int start,
        SqlTableReference? triggerTable)
    {
        var tokens = boundaries.Tokens;
        var open = start - 1;

        if (open < 1 || open >= tokens.Count || !tokens[open].IsPunctuation("("))
        {
            return null;
        }

        return AnalyzeAt(boundaries, open - 1, tokens[open].Start, triggerTable);
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

    private static int FindScopeStart(SqlStatementBoundaries boundaries, int caretIndex)
    {
        var tokens = boundaries.Tokens;
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

            if (StartsScope(boundaries, i))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// <paramref name="index"/> 開始一個查詢範圍：一句的開頭，或 SELECT。
    /// </summary>
    /// <remarks>
    /// 語句的開頭與位置分析同一條判準（<see cref="SqlStatementBoundaries.IsStatementHead"/>）：
    /// MERGE 的 <c>THEN UPDATE</c>、<c>UPDATE t⏎SET</c> 的 SET、<c>WITH (NOLOCK)</c> 都不是開頭，
    /// 範圍不會從中間切斷；BACKUP、RESTORE、THROW 這些名單寫不完的語句一樣是界線。
    ///
    /// SELECT 另外算：每一個 SELECT 都開始自己的查詢規格，看不到前面那段的資料表——
    /// <c>INSERT INTO t (|) SELECT … FROM s</c> 的資料行清單只屬於 t，
    /// <c>… UNION SELECT</c> 的 FROM 只屬於後面那一個。這兩處都不是一句的開頭。
    /// </remarks>
    private static bool StartsScope(SqlStatementBoundaries boundaries, int index)
    {
        var tokens = boundaries.Tokens;

        return (tokens[index].IsKeyword("SELECT") && !(index >= 1 && tokens[index - 1].IsPunctuation("."))) ||
            boundaries.IsStatementHead(index);
    }

    /// <summary>
    /// 從 <paramref name="start"/> 這個詞法單元起算，這一句敘述到哪裡結束（不含）。
    /// </summary>
    /// <remarks>
    /// 深度 0 的分號、<c>GO</c>、右括號，或下一個範圍的開頭（<see cref="StartsScope"/>）；
    /// 都沒有就到文字結尾。
    ///
    /// 開放給同組件是因為問這個問題的不只範圍分析：<c>SELECT … INTO #tmp</c> 的名冊要
    /// 知道那句 <c>SELECT</c> 涵蓋到哪裡，才讀得出它投影出來的資料行。各寫一份的
    /// 症狀是同一段文字在兩處切在不同的地方，而偏掉的那一份沒有任何徵兆——
    /// 只是資料來源清單多出或少掉幾張表。
    /// </remarks>
    internal static int FindStatementEnd(SqlStatementBoundaries boundaries, int start)
    {
        var tokens = boundaries.Tokens;
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

            if (i > start && StartsScope(boundaries, i))
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
    ///
    /// FROM、INTO 接不接資料來源由它所屬的動詞決定（<see cref="SqlStatementBoundaries.IntroducesDataSource"/>）：
    /// <c>RESTORE … FROM DISK</c>、<c>FETCH NEXT FROM c</c>、<c>REVOKE … FROM u</c>、<c>FETCH c INTO @a</c>
    /// 後面都不是資料表。
    /// </remarks>
    internal static IReadOnlyList<SqlTableReference> ExtractSources(
        SqlStatementBoundaries boundaries,
        int start,
        int end)
    {
        var tokens = boundaries.Tokens;
        var references = new List<SqlTableReference>();
        var paired = SqlTokenNavigator.FindPairedParentheses(tokens, start, end);
        var groupCloses = new HashSet<int>();
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
                depth += groupCloses.Contains(index) ? 0 : token.IsPunctuation("(") ? 1 : -1;
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
                    !SqlDdlTarget.IsDataSourceOn(tokens, index)) ||
                ((token.IsKeyword("FROM") || token.IsKeyword("INTO")) && !boundaries.IntroducesDataSource(index)) ||
                (IsDmlVerb(token) && boundaries.FindDmlTarget(index) != index))
            {
                index++;
                continue;
            }

            // FROM 與 INTO 後面可以是逗號分隔的清單，JOIN／APPLY／USING 只接一個。
            var allowsList = token.IsKeyword("FROM") || token.IsKeyword("INTO");
            var collects = !selectInto || !token.IsKeyword("INTO");

            // UPDATE、DELETE、MERGE 的目標寫在 TOP 子句之後（MERGE TOP (10) dbo.Loan t）；其餘的字後面不會是 TOP。
            index = SqlTokenNavigator.SkipDmlTop(tokens, index + 1, end);

            while (index < end)
            {
                index = EnterJoinedTableGroups(tokens, index, end, groupCloses);

                if (!TryParseTableReference(tokens, index, end, out var reference, out var next))
                {
                    break;
                }

                if (collects)
                {
                    references.Add(reference);
                    AddTableArguments(tokens, index, next, references);
                }

                index = next;

                while (TryParsePivot(tokens, index, end, out var pivot, out next))
                {
                    if (collects)
                    {
                        references.Add(pivot);
                    }

                    index = next;
                }

                if (!allowsList || index >= end || !tokens[index].IsPunctuation(","))
                {
                    break;
                }

                index++;
            }
        }

        RemoveAliasReferences(references);
        return references;
    }

    /// <summary>
    /// 資料列集函式的具名引數 <c>TABLE = 來源</c>：<c>VECTOR_SEARCH(TABLE = dbo.Copy AS c, …) AS s</c> 的 <c>c</c>
    /// 與函式的 <c>s</c> 同一層，選取清單與 ORDER BY 都以它限定欄位。
    /// </summary>
    /// <remarks>
    /// 認的是引數的寫法而不是函式名稱：引數清單裡以 <c>TABLE =</c> 開頭的一項就是一個資料來源。
    /// 還沒關上的括號照樣讀到 <paramref name="end"/>：游標往往就在後面的引數裡（<c>COLUMN = c.</c>）。
    /// </remarks>
    private static void AddTableArguments(
        IReadOnlyList<SqlToken> tokens,
        int start,
        int end,
        List<SqlTableReference> references)
    {
        if (start + 1 >= end || tokens[start].Kind != SqlTokenKind.Identifier)
        {
            return;
        }

        var open = start;

        while (open < end && (tokens[open].Kind == SqlTokenKind.Identifier || tokens[open].IsPunctuation(".")))
        {
            open++;
        }

        if (open >= end || !tokens[open].IsPunctuation("("))
        {
            return;
        }

        var close = SqlTokenNavigator.FindClosingParenthesis(tokens, open, end);
        var stop = close < 0 ? end : close;
        var item = open + 1;

        while (item < stop)
        {
            var itemEnd = item;

            while (itemEnd < stop && !tokens[itemEnd].IsPunctuation(","))
            {
                itemEnd = tokens[itemEnd].IsPunctuation("(") ? SqlTokenNavigator.SkipParenthesised(tokens, itemEnd, stop) : itemEnd + 1;
            }

            if (item + 2 < itemEnd &&
                tokens[item].IsKeyword("TABLE") &&
                tokens[item + 1].Value == "=" &&
                TryParseTableReference(tokens, item + 2, itemEnd, out var argument, out _))
            {
                references.Add(argument);
            }

            item = itemEnd + 1;
        }
    }

    /// <summary>
    /// 跳過資料來源位置上括起一段聯結的左括號，把對應的右括號記進 <paramref name="groupCloses"/>。
    /// </summary>
    /// <remarks>
    /// 資料來源位置上的括號只有兩種：開啟查詢的是衍生資料表（<see cref="SqlTokenNavigator.OpensQuery"/>），
    /// 其餘是 <c>FROM (Lib_Reader a JOIN Loan b ON …)</c>、<c>USING ((SELECT …) AS s JOIN Copy ON …)</c>
    /// 這種把聯結括起來的分組。分組沒有自己的範圍，裡面的來源與別名屬於這一層，所以不算深度；
    /// 當成衍生資料表整段跳過的症狀是 <c>ON</c> 之後一個別名都不列，<c>a.</c> 也沒有欄位。
    /// 還沒關上的分組一樣走進去：游標往往就在裡面。
    /// </remarks>
    private static int EnterJoinedTableGroups(
        IReadOnlyList<SqlToken> tokens,
        int index,
        int end,
        HashSet<int> groupCloses)
    {
        while (index < end && tokens[index].IsPunctuation("(") && !SqlTokenNavigator.OpensQuery(tokens, index))
        {
            var close = SqlTokenNavigator.FindClosingParenthesis(tokens, index, end);

            if (close >= 0)
            {
                groupCloses.Add(close);
            }

            index++;
        }

        return index;
    }

    /// <summary>
    /// 把「其實是別名」的那個來源從清單裡拿掉。
    /// </summary>
    /// <remarks>
    /// <c>UPDATE a SET … FROM dbo.Loan a</c> 的 <c>a</c> 不是一張叫 a 的資料表，
    /// 而是同一句 FROM 子句裡那個別名——<c>DELETE FROM a FROM dbo.Loan a</c> 同理。
    /// 收成一個具名來源的話，中繼資料層會為一個不存在的名稱查一輪、
    /// 而且每一次按鍵都查；更糟的是未限定欄位的判斷會因為「有一個來源解析不出來」
    /// 整段放棄，症狀是 <c>SET |</c> 的欄位停上去什麼提示都沒有。
    ///
    /// 判斷條件只有「單段裸名，而且同一句裡有人用這個名字當別名」：帶結構描述的
    /// <c>UPDATE dbo.Loan</c> 不可能是別名，而 T-SQL 本來就不允許同一句裡有兩個
    /// 相同的相關名稱，因此不必分辨是哪個關鍵字帶進來的。
    /// </remarks>
    private static void RemoveAliasReferences(List<SqlTableReference> references)
    {
        if (references.Count < 2)
        {
            return;
        }

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in references)
        {
            if (!string.IsNullOrEmpty(reference.Alias))
            {
                aliases.Add(reference.Alias!);
            }
        }

        if (aliases.Count == 0)
        {
            return;
        }

        references.RemoveAll(reference =>
            string.IsNullOrEmpty(reference.Alias) &&
            !reference.IsDerived &&
            reference.SchemaName is null &&
            reference.DatabaseName is null &&
            reference.ServerName is null &&
            aliases.Contains(reference.ObjectName));
    }

    /// <summary>
    /// 從 <paramref name="index"/> 讀一段 <c>PIVOT (…) 別名</c> 或 <c>UNPIVOT (…) 別名</c>：前一個來源轉出來的新來源。
    /// </summary>
    /// <remarks>
    /// 之後的子句以它的別名引用（<c>PIVOT (…) P CROSS APPLY (SELECT P.[0])</c>），不收的話 <c>P</c> 不是別名，
    /// <c>P.</c> 退回結構描述的解讀。轉出來的資料行要從前一個來源扣掉彙總與 FOR 的資料行、再加上 IN 的值，
    /// 這裡不算：當成解析不出欄位的來源，<c>SELECT *</c> 就不展開，而不是展開成轉之前那張表的欄位。
    /// 前一個來源照樣留著：PIVOT 括號裡的彙總與 FOR 引用的正是它的資料行。
    /// </remarks>
    private static bool TryParsePivot(
        IReadOnlyList<SqlToken> tokens,
        int index,
        int end,
        out SqlTableReference pivot,
        out int next)
    {
        pivot = null!;
        next = index;

        if (index + 1 >= end ||
            !(tokens[index].IsKeyword("PIVOT") || tokens[index].IsKeyword("UNPIVOT")) ||
            !tokens[index + 1].IsPunctuation("("))
        {
            return false;
        }

        var cursor = SqlTokenNavigator.SkipParenthesised(tokens, index + 1, end);
        var alias = TryReadAlias(tokens, ref cursor, end);

        if (alias is null)
        {
            return false;
        }

        pivot = new SqlTableReference(string.Empty, alias, tokens[index].Start, tokens[cursor - 1].End);
        next = cursor;
        return true;
    }

    /// <summary>從 <paramref name="index"/> 讀一個資料來源：名稱或括號、別名與後面的提示，讀到 <paramref name="end"/> 為止。</summary>
    /// <remarks>
    /// 開放給同組件是因為資料行的所屬資料表（<c>SqlColumnOwner</c>）也要讀同一種東西；
    /// 各讀一份的話，其中一邊多認得一種寫法，同一個名稱就會解出兩張表。
    /// </remarks>
    internal static bool TryParseTableReference(
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

        // 別名後面那串括號是什麼由來源的形狀決定，見 ReadAliasParentheses。
        var aliasList = AliasList.Hints;

        if (first.IsPunctuation("("))
        {
            // 衍生資料表或資料表值建構式：查不到中繼資料，但別名仍要記下來，
            // 否則後面用這個別名限定欄位時會誤判成資料表名稱。
            index = SqlTokenNavigator.SkipParenthesised(tokens, index, end);
            isDerived = true;
            aliasList = AliasList.Columns;
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

                // 資料列集函式只接資料行清單；使用者定義的函式兩種都寫得出來。
                aliasList = parts.Count == 1 && !first.IsQuoted && RowsetFunctions.Contains(parts[0])
                    ? AliasList.Columns
                    : AliasList.Either;
            }
        }
        else
        {
            return false;
        }

        // 別名之前與之後都要跳：文法把 TABLESAMPLE 與 WITH (…) 排在別名後面，
        // 而 FROM Loans WITH (NOLOCK) o 這種順序在實際指令碼裡一樣寫得出來。
        SkipTableSourceTail(tokens, ref index, end);

        var alias = TryReadAlias(tokens, ref index, end);
        var columnNames = ReadAliasParentheses(tokens, ref index, end, alias is null ? AliasList.Hints : aliasList);

        SkipTableSourceTail(tokens, ref index, end);

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
    /// 別名後面那串括號：資料行清單或舊式資料表提示，<b>兩種都要跳完</b>。
    /// </summary>
    /// <remarks>
    /// 「不讀它的內容」與「不跳過它」是兩件事，而只做前者會壞得更難看：剖析停在
    /// 括號前面，後面那個逗號就不再是來源清單的逗號，
    /// <c>FROM dbo.Loan l (NOLOCK), dbo.Copy c</c> 的 <c>dbo.Copy</c> 整個消失。
    /// <c>c.</c> 列不出欄位還算看得出來，<c>SELECT *</c> 展開才是真的糟——它以為只有
    /// 一個來源，攤出一份少了一半欄位、卻仍然執行得動的選取清單。
    ///
    /// 是哪一種先看<b>來源的形狀</b>：衍生資料表與 <see cref="RowsetFunctions"/> 只接資料行
    /// 清單，具名資料表只接提示。使用者定義的函式兩種都寫得出來——
    /// <c>dbo.fn_Loans(0) f (NOLOCK)</c> 是提示，<c>dbo.fn_Split(x) AS s (CopyNo)</c> 是資料行
    /// 清單——只有這一種看括號內容：每一項都以資料表提示開頭才是提示
    /// （<see cref="AllTableHints"/>）。整份當提示的症狀是 <c>s.</c> 列不出 <c>CopyNo</c>；整份當清單則是
    /// <c>SELECT * INTO #Temp FROM dbo.fn(x) f (NOLOCK)</c> 之後，<c>#Temp</c> 的結構只剩一個叫 NOLOCK
    /// 的欄位，假結構一路傳到預覽與 <c>INSERT INTO #Temp</c> 的整句展開。真有資料行叫 NOLOCK 時
    /// 讀成提示，代價只是少列一個名稱。
    ///
    /// 走到這裡不必再分辨資料表提示與函式引數：<c>WITH (NOLOCK)</c> 與函式自己的
    /// 引數清單都在別名<b>之前</b>就跳完了。沒有別名也不收，文法要求這串括號接在
    /// 別名後面。
    ///
    /// 括號還沒關上時當成沒寫，並把位置留在原地：使用者正打到一半，而讀一半的清單
    /// 會覆寫掉主體算得出來的名稱。
    /// </remarks>
    private static IReadOnlyList<string> ReadAliasParentheses(
        IReadOnlyList<SqlToken> tokens,
        ref int index,
        int end,
        AliasList list)
    {
        if (index >= end || !tokens[index].IsPunctuation("("))
        {
            return Array.Empty<string>();
        }

        var close = SqlTokenNavigator.FindClosingParenthesis(tokens, index, end);

        if (close < 0)
        {
            return Array.Empty<string>();
        }

        var isColumnList = list == AliasList.Columns ||
            list == AliasList.Either && !AllTableHints(tokens, index + 1, close);
        var names = isColumnList ? ReadColumnList(tokens, index + 1, close) : Array.Empty<string>();
        index = close + 1;
        return names;
    }

    /// <summary>括號裡逗號隔開的每一項都以資料表提示開頭（<c>NOLOCK</c>、<c>INDEX (ix)</c>）。</summary>
    private static bool AllTableHints(IReadOnlyList<SqlToken> tokens, int start, int end)
    {
        var itemStart = true;

        for (var index = start; index < end; index++)
        {
            var token = tokens[index];

            if (token.IsPunctuation("("))
            {
                index = SqlTokenNavigator.FindClosingParenthesis(tokens, index, end);

                if (index < 0)
                {
                    return false;
                }

                continue;
            }

            if (token.IsPunctuation(","))
            {
                itemStart = true;
                continue;
            }

            if (itemStart && (token.Kind != SqlTokenKind.Identifier || token.IsQuoted || !SqlArgumentCatalog.IsTableHint(token.Value)))
            {
                return false;
            }

            itemStart = false;
        }

        return true;
    }

    /// <summary>別名後面那串括號可能是什麼。</summary>
    private enum AliasList
    {
        /// <summary>舊式資料表提示：具名資料表，或沒寫別名。</summary>
        Hints,

        /// <summary>資料行清單：衍生資料表與資料列集函式。</summary>
        Columns,

        /// <summary>兩種都寫得出來，看內容：使用者定義的資料表值函式。</summary>
        Either,
    }

    /// <summary>
    /// 跳過接在資料來源後面的 <c>TABLESAMPLE</c> 與 <c>WITH (…)</c>。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="ReadAliasParentheses"/> 同一個理由：跳不完的話逗號清單就在這裡
    /// 斷掉。<c>TABLESAMPLE [SYSTEM] (…) [REPEATABLE (…)]</c> 三段都認，因為它們是
    /// 同一個子句拆出來的，少認一段與完全不認的症狀一模一樣。
    ///
    /// 括號配不起來時停在原地，不硬吃到敘述結尾：使用者正打到一半的
    /// <c>WITH (</c> 後面往往就是他要的東西。
    /// </remarks>
    private static void SkipTableSourceTail(IReadOnlyList<SqlToken> tokens, ref int index, int end)
    {
        SkipTableNameSuffix(tokens, ref index, end);

        while (index < end &&
               (tokens[index].IsKeyword("WITH") ||
                   tokens[index].IsKeyword("TABLESAMPLE") ||
                   tokens[index].IsKeyword("REPEATABLE")))
        {
            var open = index + 1;

            if (open < end && tokens[open].IsKeyword("SYSTEM"))
            {
                open++;
            }

            if (open >= end || !tokens[open].IsPunctuation("("))
            {
                return;
            }

            var close = SqlTokenNavigator.FindClosingParenthesis(tokens, open, end);

            if (close < 0)
            {
                return;
            }

            index = close + 1;
        }
    }

    /// <summary>
    /// 跳過資料表名稱的 <c>FOR</c> 後綴：圖形查詢的 <c>FOR PATH</c>，時態表的 <c>FOR SYSTEM_TIME …</c>
    /// （<c>AS OF x</c>、<c>FROM x TO y</c>、<c>BETWEEN x AND y</c>、<c>CONTAINED IN (x, y)</c>、<c>ALL</c>）。
    /// </summary>
    /// <remarks>
    /// 文法把它排在別名前面；跳不過的話 <c>FOR</c> 讀不成別名，<c>JOIN Loan FOR SYSTEM_TIME AS OF @d AS a</c>
    /// 的 <c>a</c> 就不見了，逗號清單也在這裡斷掉，之後的來源全部讀不到。寫到一半時停在讀得到的地方。
    /// </remarks>
    private static void SkipTableNameSuffix(IReadOnlyList<SqlToken> tokens, ref int index, int end)
    {
        if (index + 1 < end && tokens[index].IsKeyword("FOR") && tokens[index + 1].IsKeyword("PATH"))
        {
            index += 2;
            return;
        }

        if (index + 2 >= end || !tokens[index].IsKeyword("FOR") || !tokens[index + 1].IsKeyword("SYSTEM_TIME"))
        {
            return;
        }

        var cursor = index + 2;
        var kind = tokens[cursor];
        cursor++;

        if (kind.IsKeyword("AS") && cursor < end && tokens[cursor].IsKeyword("OF"))
        {
            cursor = SkipOperand(tokens, cursor + 1, end);
        }
        else if ((kind.IsKeyword("FROM") || kind.IsKeyword("BETWEEN")) && (cursor = SkipOperand(tokens, cursor, end)) < end &&
            (tokens[cursor].IsKeyword("TO") || tokens[cursor].IsKeyword("AND")))
        {
            cursor = SkipOperand(tokens, cursor + 1, end);
        }
        else if (kind.IsKeyword("CONTAINED") && cursor < end && tokens[cursor].IsKeyword("IN"))
        {
            cursor = SkipOperand(tokens, cursor + 1, end);
        }
        else if (!kind.IsKeyword("ALL"))
        {
            return;
        }

        index = cursor;
    }

    /// <summary>一個運算元：常值、變數、括號，或函式呼叫。</summary>
    private static int SkipOperand(IReadOnlyList<SqlToken> tokens, int index, int end)
    {
        if (index >= end)
        {
            return index;
        }

        if (!tokens[index].IsPunctuation("("))
        {
            index++;
        }

        return index < end && tokens[index].IsPunctuation("(")
            ? SqlTokenNavigator.SkipParenthesised(tokens, index, end)
            : index;
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
