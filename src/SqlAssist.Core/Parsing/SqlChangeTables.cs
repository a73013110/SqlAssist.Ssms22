using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// <c>inserted</c>／<c>deleted</c>：只在兩種地方看得到的資料表，欄位與它們指的那張表相同。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>DML 觸發程序的主體：指父資料表（<c>CREATE TRIGGER tr ON dbo.Loan</c> 的 Loan）。</item>
/// <item>DML 的 OUTPUT 子句：指那句 DML 的目標，不寫 FROM 就引用得到。</item>
/// </list>
/// 兩種都在時（觸發程序裡的 UPDATE … OUTPUT）各管各的：OUTPUT 子句裡的是 UPDATE 的目標，
/// 同一句 FROM 寫的 inserted 仍是觸發程序那一份。DDL 與登入觸發程序（<c>ON DATABASE</c>、
/// <c>ON ALL SERVER</c>）沒有它們。
///
/// 中繼資料查不到這兩個名字，資料表也不叫這個名字；少了這一份的症狀是 <c>FROM </c> 與
/// <c>OUTPUT </c> 之後列不出它們，<c>inserted.</c> 之後退成結構描述的解讀、一個欄位都沒有。
/// </remarks>
internal static class SqlChangeTables
{
    public static readonly IReadOnlyList<string> Names = new[] { "inserted", "deleted" };

    public static bool IsName(string name) =>
        string.Equals(name, "inserted", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "deleted", StringComparison.OrdinalIgnoreCase);

    /// <summary>沒有限定字的 <c>inserted</c>／<c>deleted</c> 來源（<c>FROM inserted i</c>）。</summary>
    public static bool IsChangeTable(SqlTableReference reference) =>
        reference.Path is { SchemaName: null, DatabaseName: null, ServerName: null } &&
        IsName(reference.ObjectName);

    /// <summary>
    /// <paramref name="index"/> 這個詞元在 DML 觸發程序的主體裡時，觸發程序的父資料表；不在時是 null。
    /// </summary>
    /// <remarks>
    /// <c>CREATE TRIGGER</c> 必須是批次的第一句，主體寫到批次結束，所以往回找到 GO 為止。
    /// </remarks>
    public static SqlTableReference? FindTriggerTable(IReadOnlyList<SqlToken> tokens, int index)
    {
        for (var trigger = Math.Min(index, tokens.Count - 1); trigger >= 1; trigger--)
        {
            if (tokens[trigger].IsKeyword("GO"))
            {
                return null;
            }

            if (!tokens[trigger].IsKeyword("TRIGGER") ||
                !(tokens[trigger - 1].IsKeyword("CREATE") || tokens[trigger - 1].IsKeyword("ALTER")))
            {
                continue;
            }

            var on = trigger + 1;

            while (on < tokens.Count && !tokens[on].IsKeyword("ON") &&
                (tokens[on].Kind == SqlTokenKind.Identifier || tokens[on].IsPunctuation(".")))
            {
                on++;
            }

            if (on + 1 >= index ||
                !SqlDdlTarget.IsDataSourceOn(tokens, on) ||
                tokens[on + 1].IsKeyword("DATABASE") ||
                tokens[on + 1].IsKeyword("ALL") ||
                !SqlScopeAnalyzer.TryParseTableReference(tokens, on + 1, tokens.Count, out var table, out _) ||
                table.Path is null)
            {
                return null;
            }

            // 名稱之後的 AFTER、INSTEAD 會被讀成別名；父資料表只要路徑。
            return new SqlTableReference(table.Path, alias: null, table.Start, table.End);
        }

        return null;
    }

    /// <summary>
    /// <paramref name="index"/> 這個詞元在 DML 的 OUTPUT 子句裡時，那句 DML 的目標；不在時是 null。
    /// </summary>
    /// <remarks>
    /// 目標照寫法讀出來：<c>UPDATE l SET … FROM dbo.Loan l</c> 的 l 是別名，由呼叫端在範圍裡解開。
    /// MERGE 的動作（<c>THEN DELETE OUTPUT</c>）往回走到這一句的開頭，目標是 MERGE 的那一個。
    /// </remarks>
    internal static SqlTableReference? FindOutputTarget(SqlStatementBoundaries boundaries, int index)
    {
        var tokens = boundaries.Tokens;
        var output = boundaries.FindDmlOutput(index);

        if (output < 0)
        {
            return null;
        }

        var verb = -1;

        for (var position = output - 1; position >= 0; position--)
        {
            var token = tokens[position];

            if (token.IsPunctuation(")"))
            {
                position = SqlTokenNavigator.FindOpeningParenthesis(tokens, position);

                if (position < 0)
                {
                    break;
                }

                continue;
            }

            if (token.IsPunctuation(";") || token.IsPunctuation("(") || token.IsKeyword("GO"))
            {
                break;
            }

            if (IsDmlVerb(tokens, position))
            {
                verb = position;
            }

            if (boundaries.IsStatementHead(position))
            {
                break;
            }
        }

        return verb >= 0 ? ReadTarget(tokens, verb) : null;
    }

    private static bool IsDmlVerb(IReadOnlyList<SqlToken> tokens, int index) =>
        (tokens[index].IsKeyword("INSERT") ||
            tokens[index].IsKeyword("UPDATE") ||
            tokens[index].IsKeyword("DELETE") ||
            tokens[index].IsKeyword("MERGE")) &&
        !(index >= 1 && tokens[index - 1].IsPunctuation(".")) &&
        !(index + 1 < tokens.Count && tokens[index + 1].IsPunctuation("("));

    /// <summary>動詞之後的目標：跳過 <c>TOP (n) [PERCENT]</c> 與 INSERT、MERGE 的 INTO、DELETE 的 FROM。</summary>
    private static SqlTableReference? ReadTarget(IReadOnlyList<SqlToken> tokens, int verb)
    {
        var index = verb + 1;

        if (index < tokens.Count && tokens[index].IsKeyword("TOP"))
        {
            index++;

            if (index < tokens.Count && tokens[index].IsPunctuation("("))
            {
                index = SqlTokenNavigator.SkipParenthesised(tokens, index, tokens.Count);
            }

            if (index < tokens.Count && tokens[index].IsKeyword("PERCENT"))
            {
                index++;
            }
        }

        if (index < tokens.Count && (tokens[index].IsKeyword("INTO") || tokens[index].IsKeyword("FROM")))
        {
            index++;
        }

        return SqlScopeAnalyzer.TryParseTableReference(tokens, index, tokens.Count, out var target, out _) ? target : null;
    }
}
