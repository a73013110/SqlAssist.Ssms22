using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 從指令碼裡讀出暫存資料表與資料表變數的資料行清單。
/// </summary>
/// <remarks>
/// 認<b>帶著資料行定義</b>的兩種寫法：<c>CREATE TABLE #tmp (…)</c> 與
/// <c>DECLARE @tmp [AS] TABLE (…)</c>（函式的 <c>RETURNS @tmp TABLE (…)</c> 是同一個
/// 形狀，因此免費一起認得）；資料表值參數（<c>@rows dbo.LoanRows READONLY</c>）的資料行在型別定義裡，
/// 指令碼自己寫了 <c>CREATE TYPE … AS TABLE (…)</c> 才讀得出來。<c>SELECT … INTO #tmp</c> 不在這一份裡：它的資料行
/// 要把整段選取清單遞迴攤平，而這裡是一趟走完的線性掃描，收得起來的只有形狀就看
/// 得出來的東西。那一種由 <see cref="SqlColumnSourceResolver.FindScriptTable"/>
/// 延後投影，兩者在那裡合成同一個型別。
///
/// <c>CREATE TABLE</c> 這兩個字是必要條件而不是修飾：<c>INSERT INTO #tmp (a, b)</c>
/// 的形狀與資料行清單一模一樣，少了前綴就會把使用者剛寫的 INSERT 讀成一份宣告，
/// 而那份「宣告」裡每個資料行都沒有型別。
///
/// 一份宣告都沒有時共用同一份空名冊：這條路徑在每一次按鍵上，
/// 而那正是絕大多數指令碼的情形。
/// </remarks>
public static class SqlScriptTableCollector
{
    private static readonly Dictionary<string, SqlScriptTable> NoTables =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>資料行清單裡不是資料行的項目，開頭第一個字就看得出來。</summary>
    private static readonly HashSet<string> ConstraintKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CONSTRAINT", "PRIMARY", "UNIQUE", "FOREIGN", "CHECK", "INDEX", "PERIOD"
        };

    /// <summary>索引鍵清單裡的排序方向，不是資料行名稱。</summary>
    private static readonly HashSet<string> SortDirections =
        new(StringComparer.OrdinalIgnoreCase) { "ASC", "DESC" };

    /// <summary>
    /// 收集整份指令碼宣告過的資料表。
    /// </summary>
    /// <remarks>
    /// 不限定在游標所在的批次裡找，理由與 CTE 名冊相同：這種名稱在一份指令碼裡
    /// 幾乎不會重複，而要正確劃出批次邊界得再維護一套規則。
    /// 同名時保留先出現的那一個。
    /// </remarks>
    public static IReadOnlyDictionary<string, SqlScriptTable> Collect(IReadOnlyList<SqlToken> tokens)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        Dictionary<string, SqlScriptTable>? result = null;

        for (var index = 0; index < tokens.Count; index++)
        {
            var name = ReadDeclaredName(tokens, index, out var listStart);

            if (name is null)
            {
                continue;
            }

            var listEnd = SqlTokenNavigator.FindClosingParenthesis(tokens, listStart, tokens.Count);

            // 括號還沒關起來就是他正在打這份宣告。這一輪當它不存在，
            // 打完之後下一次按鍵就有了。
            if (listEnd < 0)
            {
                continue;
            }

            result ??= new Dictionary<string, SqlScriptTable>(StringComparer.OrdinalIgnoreCase);

            if (!result.ContainsKey(name))
            {
                result.Add(
                    name,
                    new SqlScriptTable(
                        name,
                        ReadColumns(tokens, listStart + 1, listEnd),
                        tokens[index].Start,
                        tokens[listEnd].End));
            }

            index = listEnd;
        }

        for (var index = 0; index < tokens.Count; index++)
        {
            if (ReadTableParameter(tokens, index, out var typeName, out var readOnly) is not { } name)
            {
                continue;
            }

            result ??= new Dictionary<string, SqlScriptTable>(StringComparer.OrdinalIgnoreCase);

            if (!result.ContainsKey(name))
            {
                result.Add(
                    name,
                    new SqlScriptTable(name, () => ReadTypeColumns(tokens, typeName), tokens[index].Start, tokens[readOnly].End));
            }

            index = readOnly;
        }

        return result ?? NoTables;
    }

    /// <summary>
    /// <paramref name="index"/> 是資料表值參數（<c>@rows dbo.LoanRows READONLY</c>）時傳回參數名稱。
    /// </summary>
    /// <remarks>
    /// <c>READONLY</c> 只寫在資料表型別的參數上，憑它就分得出不是純量；<c>DECLARE @t dbo.LoanRows</c> 沒有它，
    /// 只看文字分不出型別是資料表還是別名型別，不收。少了這一條的症狀是程序裡 <c>FROM </c> 之後列不出自己的參數。
    /// </remarks>
    /// <param name="typeName">型別名稱的最後一段。</param>
    /// <param name="readOnly"><c>READONLY</c> 的位置。</param>
    private static string? ReadTableParameter(IReadOnlyList<SqlToken> tokens, int index, out string typeName, out int readOnly)
    {
        typeName = string.Empty;
        readOnly = -1;

        if (tokens[index].Kind != SqlTokenKind.Variable)
        {
            return null;
        }

        var type = index + 1 < tokens.Count && tokens[index + 1].IsKeyword("AS") ? index + 2 : index + 1;
        var last = type;

        while (last + 2 < tokens.Count && tokens[last + 1].IsPunctuation(".") && tokens[last + 2].Kind == SqlTokenKind.Identifier)
        {
            last += 2;
        }

        if (last + 1 >= tokens.Count ||
            tokens[type].Kind != SqlTokenKind.Identifier ||
            !tokens[last + 1].IsKeyword("READONLY"))
        {
            return null;
        }

        typeName = tokens[last].Value;
        readOnly = last + 1;
        return tokens[index].Value;
    }

    /// <summary>
    /// 指令碼自己寫的 <c>CREATE TYPE … AS TABLE (…)</c> 裡 <paramref name="typeName"/> 的資料行；沒有這份定義時是空的。
    /// </summary>
    /// <remarks>型別在資料庫裡的那一份要問中繼資料，這裡讀不到，只留名稱。</remarks>
    private static IReadOnlyList<SqlScriptColumn> ReadTypeColumns(IReadOnlyList<SqlToken> tokens, string typeName)
    {
        for (var open = 0; open < tokens.Count; open++)
        {
            if (FindDefinitionName(tokens, open) is not { } name ||
                name.Start < 1 ||
                !tokens[name.Start - 1].IsKeyword("TYPE") ||
                !string.Equals(tokens[name.End - 1].Value, typeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var close = SqlTokenNavigator.FindClosingParenthesis(tokens, open, tokens.Count);

            if (close >= 0)
            {
                return ReadColumns(tokens, open + 1, close);
            }
        }

        return Array.Empty<SqlScriptColumn>();
    }

    /// <summary>
    /// 游標所在的資料表定義（<see cref="FindDefinitionName"/>）定義的是 <paramref name="target"/> 時，那份定義寫出來的資料行；
    /// 不在定義裡、定義的是別張表或一欄都還沒寫時是 null。
    /// </summary>
    /// <remarks>
    /// <c>PRIMARY KEY (|</c>、<c>INDEX ix (|</c> 與指回自己的 <c>REFERENCES t (|</c> 要的是同一份括號裡的資料行：
    /// 一般資料表那時還不存在，或中繼資料還是改之前的樣子；暫存資料表的括號沒關上前也不在名冊裡。
    /// 括號關上時整份都算（主索引鍵可以寫在資料行前面），還開著就只讀到游標為止——後面的文字屬於別的語句。
    /// </remarks>
    /// <param name="position">游標位置。</param>
    public static SqlScriptTable? FindDefinition(IReadOnlyList<SqlToken> tokens, int position, SqlTableReference target)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        var before = -1;

        while (before + 1 < tokens.Count && tokens[before + 1].Start < position)
        {
            before++;
        }

        for (var open = SqlTokenNavigator.FindUnclosedParenthesis(tokens, before);
             open >= 2;
             open = SqlTokenNavigator.FindUnclosedParenthesis(tokens, open - 1))
        {
            if (FindDefinitionName(tokens, open) is not { } name)
            {
                continue;
            }

            if (!SqlScopeAnalyzer.TryParseTableReference(tokens, name.Start, name.End, out var defined, out _) ||
                !IsSameTable(defined, target))
            {
                return null;
            }

            var close = SqlTokenNavigator.FindClosingParenthesis(tokens, open, tokens.Count);
            var end = close >= 0 ? close : before + 1;
            var columns = ReadColumns(tokens, open + 1, end);

            return columns.Count == 0
                ? null
                : new SqlScriptTable(defined.ObjectName, columns, tokens[name.Start].Start, tokens[end - 1].End);
        }

        return null;
    }

    /// <summary>
    /// <paramref name="open"/> 這個左括號是一份資料表定義的資料行清單時，定義的名稱所在的詞元範圍（不含 End）；不是時是 null。
    /// </summary>
    /// <remarks>
    /// 三種寫法的括號裡是同一套文法：<c>CREATE TABLE t (</c>、<c>@t [AS] TABLE (</c>（<c>DECLARE</c> 與
    /// <c>RETURNS</c>）、<c>CREATE TYPE t AS TABLE (</c>。
    /// </remarks>
    public static (int Start, int End)? FindDefinitionName(IReadOnlyList<SqlToken> tokens, int open)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if (open < 2 || !tokens[open].IsPunctuation("("))
        {
            return null;
        }

        if (tokens[open - 1].IsKeyword("TABLE"))
        {
            var name = tokens[open - 2].IsKeyword("AS") ? open - 3 : open - 2;

            if (name >= 0 && tokens[name].Kind == SqlTokenKind.Variable)
            {
                return (name, name + 1);
            }

            if (name < 0 || name == open - 2)
            {
                return null;
            }

            var typeStart = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, name);

            return typeStart >= 2 && tokens[typeStart - 1].IsKeyword("TYPE") && tokens[typeStart - 2].IsKeyword("CREATE")
                ? (typeStart, name + 1)
                : null;
        }

        var start = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, open - 1);

        return start >= 2 && tokens[start - 1].IsKeyword("TABLE") && tokens[start - 2].IsKeyword("CREATE")
            ? (start, open)
            : null;
    }

    /// <summary>名稱相同、寫出來的結構描述與資料庫不衝突。</summary>
    private static bool IsSameTable(SqlTableReference defined, SqlTableReference target) =>
        string.Equals(defined.ObjectName, target.ObjectName, StringComparison.OrdinalIgnoreCase) &&
        Agrees(defined.SchemaName, target.SchemaName) &&
        Agrees(defined.DatabaseName, target.DatabaseName);

    private static bool Agrees(string? left, string? right) =>
        left is null || right is null || string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="index"/> 是不是一份資料表宣告的開頭。
    /// </summary>
    /// <param name="listStart">資料行清單的左括號位置。</param>
    private static string? ReadDeclaredName(IReadOnlyList<SqlToken> tokens, int index, out int listStart)
    {
        listStart = -1;

        // CREATE TABLE #tmp ( … )。井號開頭是必要條件：一般資料表在中繼資料裡，
        // 拿指令碼裡這一份去蓋掉它等於用「正要建立的樣子」回答「現在長什麼樣」。
        if (tokens[index].IsKeyword("CREATE") &&
            index + 3 < tokens.Count &&
            tokens[index + 1].IsKeyword("TABLE") &&
            tokens[index + 2].Kind == SqlTokenKind.Identifier &&
            tokens[index + 2].Value.Length > 1 &&
            tokens[index + 2].Value[0] == '#' &&
            tokens[index + 3].IsPunctuation("("))
        {
            listStart = index + 3;
            return tokens[index + 2].Value;
        }

        // DECLARE @tmp [AS] TABLE ( … ) 與 RETURNS @tmp TABLE ( … )。認的是
        // 「變數 [AS] TABLE (」這個形狀本身：前面那個字不改變它宣告了什麼。
        // 資料表型別的參數（@t dbo.MyType READONLY）沒有這個形狀，見 ReadTableParameter。
        // AS 與純量變數的 DECLARE @n AS INT 一樣可有可無，少認它的症狀是那張表的別名一個欄位都沒有。
        var table = index + 1 < tokens.Count && tokens[index + 1].IsKeyword("AS") ? index + 2 : index + 1;

        if (tokens[index].Kind == SqlTokenKind.Variable &&
            table + 1 < tokens.Count &&
            tokens[table].IsKeyword("TABLE") &&
            tokens[table + 1].IsPunctuation("("))
        {
            listStart = table + 1;
            return tokens[index].Value;
        }

        return null;
    }

    private static IReadOnlyList<SqlScriptColumn> ReadColumns(
        IReadOnlyList<SqlToken> tokens,
        int start,
        int end)
    {
        var columns = new List<SqlScriptColumn>();
        var primaryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = start;

        while (index < end)
        {
            var itemEnd = SqlTokenNavigator.FindListItemEnd(tokens, index, end);

            if (itemEnd > index)
            {
                ReadItem(tokens, index, itemEnd, columns, primaryKeys);
            }

            index = itemEnd + 1;
        }

        if (primaryKeys.Count == 0)
        {
            return columns;
        }

        for (var position = 0; position < columns.Count; position++)
        {
            if (!columns[position].IsPrimaryKey && primaryKeys.Contains(columns[position].Name))
            {
                columns[position] = columns[position].AsPrimaryKey();
            }
        }

        return columns;
    }

    private static void ReadItem(
        IReadOnlyList<SqlToken> tokens,
        int start,
        int end,
        List<SqlScriptColumn> columns,
        HashSet<string> primaryKeys)
    {
        var first = tokens[start];

        if (first.Kind != SqlTokenKind.Identifier)
        {
            return;
        }

        if (!first.IsQuoted && ConstraintKeywords.Contains(first.Value))
        {
            ReadTableConstraint(tokens, start, end, primaryKeys);
            return;
        }

        var cursor = start + 1;

        // 計算資料行（Total AS Qty * Price）的型別要靠運算式推導，讀文字推不出來。
        // 它本來就插不進去，因此只記下「這是計算資料行」就夠了。
        if (cursor < end && tokens[cursor].IsKeyword("AS"))
        {
            columns.Add(new SqlScriptColumn(
                first.Value,
                string.Empty,
                isNullable: true,
                hasDefault: false,
                isIdentity: false,
                isComputed: true,
                isPrimaryKey: false));
            return;
        }

        var typeStart = cursor;
        cursor = SqlTokenNavigator.SkipDataType(tokens, cursor, end);

        var isNullable = true;
        var hasDefault = false;
        var isIdentity = false;
        var isPrimaryKey = false;

        for (var index = cursor; index < end; index++)
        {
            var token = tokens[index];

            // IDENTITY(1,1)、DEFAULT (0)、CHECK (…) 的括號整組跳過：
            // 裡面的字是引數而不是資料行選項。
            if (token.IsPunctuation("("))
            {
                index = SqlTokenNavigator.SkipParenthesised(tokens, index, end) - 1;
                continue;
            }

            if (token.IsKeyword("IDENTITY"))
            {
                isIdentity = true;
            }
            else if (token.IsKeyword("DEFAULT"))
            {
                hasDefault = true;
            }
            else if (token.IsKeyword("PRIMARY"))
            {
                isPrimaryKey = true;
            }
            else if (token.IsKeyword("NULL"))
            {
                isNullable = index == 0 || !tokens[index - 1].IsKeyword("NOT");
            }
        }

        columns.Add(new SqlScriptColumn(
            first.Value,
            Describe(tokens, typeStart, cursor),
            isNullable && !isPrimaryKey,
            hasDefault,
            isIdentity,
            isComputed: false,
            isPrimaryKey));
    }

    /// <summary>
    /// 讀出資料表層級 <c>PRIMARY KEY (…)</c> 指到的資料行。
    /// </summary>
    /// <remarks>
    /// 認的是 <c>PRIMARY KEY</c> 之後的第一組括號：前面的 <c>CONSTRAINT PK_x</c>
    /// 與後面的 <c>CLUSTERED</c> 都不改變它是什麼。其他條件約束（UNIQUE、FOREIGN
    /// KEY、CHECK）在這裡沒有事情要做，讀完就走。
    /// </remarks>
    private static void ReadTableConstraint(
        IReadOnlyList<SqlToken> tokens,
        int start,
        int end,
        HashSet<string> primaryKeys)
    {
        for (var index = start; index + 1 < end; index++)
        {
            if (!tokens[index].IsKeyword("PRIMARY") || !tokens[index + 1].IsKeyword("KEY"))
            {
                continue;
            }

            var open = index + 2;

            while (open < end && !tokens[open].IsPunctuation("("))
            {
                open++;
            }

            var close = open < end
                ? SqlTokenNavigator.FindClosingParenthesis(tokens, open, end)
                : -1;

            if (close < 0)
            {
                return;
            }

            for (var column = open + 1; column < close; column++)
            {
                var token = tokens[column];

                if (token.Kind == SqlTokenKind.Identifier &&
                    (token.IsQuoted || !SortDirections.Contains(token.Value)))
                {
                    primaryKeys.Add(token.Value);
                }
            }

            return;
        }
    }

    /// <summary>
    /// 把一段詞元拼回接近原文的字串。
    /// </summary>
    /// <remarks>
    /// 詞法單元不帶空白，直接串起來會得到 <c>NOTNULL</c>，中間一律加空白又會得到
    /// <c>NVARCHAR ( 20 )</c>。型別裡的括號、逗號與點號一律貼著兩邊寫，
    /// 那正好是這裡唯一要輸出的東西——原文寫成 <c>DECIMAL(18, 2)</c> 時輸出
    /// <c>DECIMAL(18,2)</c>，兩種寫法在展開後的註解裡因此長得一樣。
    /// </remarks>
    private static string Describe(IReadOnlyList<SqlToken> tokens, int start, int end)
    {
        var builder = new StringBuilder();

        for (var index = start; index < end; index++)
        {
            var token = tokens[index];

            if (builder.Length > 0 && NeedsSpace(tokens[index - 1], token))
            {
                builder.Append(' ');
            }

            builder.Append(token.Text);
        }

        return builder.ToString();
    }

    private static bool NeedsSpace(SqlToken previous, SqlToken current)
    {
        return !current.IsPunctuation("(") &&
               !current.IsPunctuation(")") &&
               !current.IsPunctuation(",") &&
               !current.IsPunctuation(".") &&
               !previous.IsPunctuation("(") &&
               !previous.IsPunctuation(",") &&
               !previous.IsPunctuation(".");
    }
}
