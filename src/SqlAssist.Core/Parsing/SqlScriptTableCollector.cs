using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 從指令碼裡讀出暫存資料表與資料表變數的資料行清單。
/// </summary>
/// <remarks>
/// 只認<b>帶著資料行定義</b>的兩種寫法：<c>CREATE TABLE #tmp (…)</c> 與
/// <c>DECLARE @tmp TABLE (…)</c>（函式的 <c>RETURNS @tmp TABLE (…)</c> 是同一個
/// 形狀，因此免費一起認得）。<c>SELECT … INTO #tmp</c> 不在裡面：那裡沒有型別，
/// 而少了型別的 <c>INSERT</c> 骨架會替使用者猜錯字面值——名稱那一份仍然照列，
/// 見 <c>SqlScriptDataSourceSuggestions</c>。
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

        return result ?? NoTables;
    }

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

        // DECLARE @tmp TABLE ( … ) 與 RETURNS @tmp TABLE ( … )。認的是
        // 「變數 TABLE (」這個形狀本身：前面那個字不改變它宣告了什麼，
        // 而 DECLARE @t dbo.MyType READONLY 這種資料表型別參數沒有這個形狀。
        if (tokens[index].Kind == SqlTokenKind.Variable &&
            index + 2 < tokens.Count &&
            tokens[index + 1].IsKeyword("TABLE") &&
            tokens[index + 2].IsPunctuation("("))
        {
            listStart = index + 2;
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
            var itemEnd = FindItemEnd(tokens, index, end);

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

    /// <summary>資料行清單裡下一個深度 0 的逗號位置。</summary>
    private static int FindItemEnd(IReadOnlyList<SqlToken> tokens, int start, int end)
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

        // 型別可能帶結構描述（dbo.MyType），也可能帶長度或有效位數。
        while (cursor < end && tokens[cursor].Kind == SqlTokenKind.Identifier)
        {
            cursor++;

            if (cursor < end && tokens[cursor].IsPunctuation("."))
            {
                cursor++;
                continue;
            }

            break;
        }

        if (cursor < end && tokens[cursor].IsPunctuation("("))
        {
            cursor = SqlTokenNavigator.SkipParenthesised(tokens, cursor, end);
        }

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
