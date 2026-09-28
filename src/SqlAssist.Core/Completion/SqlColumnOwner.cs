using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 文法指定游標這一格是哪一張資料表的資料行。
/// </summary>
/// <remarks>
/// 與限定字是同一件事，只是限定字省略了：<c>UPDATE t SET |</c> 與 <c>t.|</c> 要的是同一份
/// 資料行，所以全文分析以同一條路把它換成 <see cref="CompletionTarget.Column"/>。
/// 位置有這幾種，各自都只寫得出那張表的資料行：
///
/// <list type="bullet">
/// <item><c>UPDATE [TOP (n)] t [WITH (…)] SET |</c> 與指派的逗號之後；MERGE 的
/// <c>THEN UPDATE SET |</c> 屬於 MERGE 的目標。</item>
/// <item><c>INSERT [INTO] t (|</c> 與逗號之後；MERGE 的 <c>THEN INSERT (|</c>。</item>
/// <item>索引鍵清單 <c>CREATE INDEX ix ON t (|</c>、<c>CREATE STATISTICS s ON t (|</c> 與
/// <c>INCLUDE (|</c>；外部索引鍵的 <c>REFERENCES u (|</c>。</item>
/// <item><c>ALTER TABLE t ALTER COLUMN |</c>、<c>DROP COLUMN |</c>。</item>
/// </list>
///
/// 不在裡面的都有理由：<c>ORDER BY |</c>、<c>GROUP BY |</c> 接得了運算式與序號；
/// <c>EXEC p |</c> 的位置引數可以直接寫常值，<c>WITH</c> 也接在那裡（打 <c>@</c> 就列參數）；
/// <c>CREATE TABLE</c> 的條件約束清單屬於一張還不存在的表。
///
/// 只讀游標前的詞元，回報的是寫在那裡的名稱。<c>UPDATE l SET … FROM dbo.Loan l</c>
/// 的 <c>l</c> 是別名，要到看得見游標後方的全文分析才解得開。
/// </remarks>
internal static class SqlColumnOwner
{
    /// <summary>游標這一格的資料行屬於哪一個資料來源；不是資料行的位置時回傳 null。</summary>
    /// <param name="tokens">游標<b>之前</b>、不含正在輸入的那個詞元的詞法單元。</param>
    /// <param name="caret">位置分析對游標處的回報。</param>
    public static SqlTableReference? Find(IReadOnlyList<SqlToken> tokens, SqlKeywordPosition caret)
    {
        var last = tokens.Count - 1;

        if (last < 0)
        {
            return null;
        }

        if (caret == SqlKeywordPosition.SetTarget)
        {
            return FindUpdateTarget(tokens, last);
        }

        if (caret == SqlKeywordPosition.AlterTableColumn)
        {
            return FindAlterTableTarget(tokens, last);
        }

        return FindColumnListOwner(tokens, last);
    }

    /// <summary>指派清單所屬的 UPDATE 目標；<c>SET NOCOUNT</c> 這種工作階段選項沒有。</summary>
    private static SqlTableReference? FindUpdateTarget(IReadOnlyList<SqlToken> tokens, int last)
    {
        var set = FindSetKeyword(tokens, last);

        if (set < 2)
        {
            return null;
        }

        // UPDATE 與 SET 之間沒有名稱的只有 MERGE 的動作。
        if (tokens[set - 1].IsKeyword("UPDATE"))
        {
            return tokens[set - 2].IsKeyword("THEN") ? FindMergeTarget(tokens, set - 2) : null;
        }

        var nameEnd = SkipGroupAfter(tokens, set - 1, "WITH");
        var nameStart = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, nameEnd);
        var verb = SkipTopBackward(tokens, nameStart - 1);

        return verb >= 0 && tokens[verb].IsKeyword("UPDATE") ? Read(tokens, nameStart, set) : null;
    }

    /// <summary>往回找指派清單的 SET；途中的括號整組跳過，沒關上的左括號或分號表示不在清單裡。</summary>
    private static int FindSetKeyword(IReadOnlyList<SqlToken> tokens, int last)
    {
        for (var index = last; index >= 0; index--)
        {
            var token = tokens[index];

            if (token.IsPunctuation(")"))
            {
                index = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

                if (index < 0)
                {
                    return -1;
                }

                continue;
            }

            if (token.IsPunctuation("(") || token.IsPunctuation(";"))
            {
                return -1;
            }

            if (token.IsKeyword("SET"))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>還開著的左括號是一份資料行清單時，那份清單所屬的資料表。</summary>
    private static SqlTableReference? FindColumnListOwner(IReadOnlyList<SqlToken> tokens, int last)
    {
        // 清單的一項剛開始：左括號本身或逗號之後。一項寫到一半（ON t (a |）是 ASC、DESC 的位置。
        if (!tokens[last].IsPunctuation("(") && !tokens[last].IsPunctuation(","))
        {
            return null;
        }

        var open = SqlTokenNavigator.FindUnclosedParenthesis(tokens, last);

        if (open < 2)
        {
            return null;
        }

        if (tokens[open - 1].IsKeyword("INSERT"))
        {
            return tokens[open - 2].IsKeyword("THEN") ? FindMergeTarget(tokens, open - 2) : null;
        }

        // INCLUDE 接在索引鍵清單後面，資料表是同一張。
        if (tokens[open - 1].IsKeyword("INCLUDE"))
        {
            var keys = tokens[open - 2].IsPunctuation(")")
                ? SqlTokenNavigator.FindOpeningParenthesis(tokens, open - 2)
                : -1;

            return keys < 0 ? null : FindNamedListOwner(tokens, keys);
        }

        return FindNamedListOwner(tokens, open);
    }

    /// <summary>左括號緊接在資料表名稱後面，而名稱前面是指定那張表的字。</summary>
    private static SqlTableReference? FindNamedListOwner(IReadOnlyList<SqlToken> tokens, int open)
    {
        var nameEnd = open - 1;

        if (nameEnd < 1 || tokens[nameEnd].Kind is not (SqlTokenKind.Identifier or SqlTokenKind.Variable))
        {
            return null;
        }

        var nameStart = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, nameEnd);
        var before = nameStart - 1;

        if (before < 0)
        {
            return null;
        }

        if (tokens[before].IsKeyword("REFERENCES") || SqlDdlTarget.IsDataSourceOn(tokens, before))
        {
            return Read(tokens, nameStart, open);
        }

        // INSERT [TOP (n)] [INTO] t (；SELECT … INTO 的新資料表後面不接資料行清單。
        if (tokens[before].IsKeyword("INTO"))
        {
            before--;
        }

        var verb = SkipTopBackward(tokens, before);

        return verb >= 0 && tokens[verb].IsKeyword("INSERT") ? Read(tokens, nameStart, open) : null;
    }

    /// <summary><c>ALTER TABLE t ALTER COLUMN</c>／<c>DROP COLUMN</c> 的 <c>t</c>；位置分析已認過這個形狀。</summary>
    private static SqlTableReference? FindAlterTableTarget(IReadOnlyList<SqlToken> tokens, int last)
    {
        var nameEnd = last - 2;

        if (nameEnd < 2)
        {
            return null;
        }

        var nameStart = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, nameEnd);

        return nameStart >= 2 && tokens[nameStart - 1].IsKeyword("TABLE") && tokens[nameStart - 2].IsKeyword("ALTER")
            ? Read(tokens, nameStart, last - 1)
            : null;
    }

    /// <summary>
    /// <paramref name="then"/> 這個 <c>THEN</c> 所屬 MERGE 的目標：<c>MERGE [TOP (n)] [INTO] t</c>。
    /// </summary>
    /// <remarks>動作寫的是目標的資料行，來源（<c>USING</c>）只出現在指派的右邊。</remarks>
    private static SqlTableReference? FindMergeTarget(IReadOnlyList<SqlToken> tokens, int then)
    {
        for (var index = then - 1; index >= 0; index--)
        {
            var token = tokens[index];

            if (token.IsPunctuation(")"))
            {
                index = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

                if (index < 0)
                {
                    return null;
                }

                continue;
            }

            if (token.IsPunctuation(";"))
            {
                return null;
            }

            if (!token.IsKeyword("MERGE"))
            {
                continue;
            }

            var target = index + 1;

            if (target < then && tokens[target].IsKeyword("TOP") &&
                target + 1 < then && tokens[target + 1].IsPunctuation("("))
            {
                target = SqlTokenNavigator.SkipParenthesised(tokens, target + 1, then);

                if (target < then && tokens[target].IsKeyword("PERCENT"))
                {
                    target++;
                }
            }

            if (target < then && tokens[target].IsKeyword("INTO"))
            {
                target++;
            }

            return Read(tokens, target, then);
        }

        return null;
    }

    /// <summary><paramref name="index"/> 是一組括號的右括號而且前面是 <paramref name="keyword"/> 時，跳到那個字之前。</summary>
    private static int SkipGroupAfter(IReadOnlyList<SqlToken> tokens, int index, string keyword)
    {
        if (index < 0 || !tokens[index].IsPunctuation(")"))
        {
            return index;
        }

        var open = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

        return open >= 1 && tokens[open - 1].IsKeyword(keyword) ? open - 2 : -1;
    }

    /// <summary>從 <paramref name="index"/> 往回跳過 <c>TOP (n) [PERCENT]</c>，回傳它前面那個詞元。</summary>
    private static int SkipTopBackward(IReadOnlyList<SqlToken> tokens, int index)
    {
        if (index >= 0 && tokens[index].IsKeyword("PERCENT"))
        {
            index--;
        }

        return SkipGroupAfter(tokens, index, "TOP");
    }

    private static SqlTableReference? Read(IReadOnlyList<SqlToken> tokens, int start, int end)
    {
        return start >= 0 && SqlScopeAnalyzer.TryParseTableReference(tokens, start, end, out var reference, out _)
            ? reference
            : null;
    }
}
