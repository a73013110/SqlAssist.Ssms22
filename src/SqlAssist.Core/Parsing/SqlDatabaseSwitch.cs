using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 指令碼裡的 <c>USE</c> 指名的資料庫。
/// </summary>
/// <remarks>
/// 這一份<b>不決定</b>建議清單要用哪一個資料庫——那是連線說了算，由 Ssms22 那一層
/// 向 SSMS 問。文字只用來決定<b>什麼時候該再問一次</b>：連線的資料庫只會因為
/// 執行過 <c>USE</c>（或使用者自己換）而改變，而執行過的 <c>USE</c> 一定寫在這份
/// 指令碼裡。認錯的代價因此只是多問一次連線，不會變成拿另一個資料庫的物件回答。
///
/// 猜寬一點也是同一個理由：<c>USE</c> 是敘述的開頭，但它前面不一定有分號或
/// <c>GO</c>（<c>SET NOCOUNT ON</c> 換行之後接 <c>USE</c> 完全合法），因此只排除
/// 明顯落在運算式或名稱裡的位置。註解與字串裡的 USE 不會成為詞法單元，
/// 不必另外排除。
/// </remarks>
public static class SqlDatabaseSwitch
{
    /// <summary>
    /// 找出 <paramref name="position"/> 之前最後一個 <c>USE</c> 指名的資料庫。
    /// </summary>
    /// <returns>資料庫名稱（已去掉方括號）；這一段文字沒有 <c>USE</c> 時為 null。</returns>
    public static string? FindLast(IReadOnlyList<SqlToken> tokens, int position)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        // 從後往前找：算數的是最後一個，前面那幾個早就被它蓋過去了。
        for (var index = tokens.Count - 2; index >= 0; index--)
        {
            var token = tokens[index];

            if (token.Start >= position)
            {
                continue;
            }

            if (!token.IsKeyword("USE") || (index > 0 && !CanPrecedeStatement(tokens[index - 1])))
            {
                continue;
            }

            var name = tokens[index + 1];

            // OPTION (USE PLAN N'…') 的 PLAN 不是資料庫名稱；加了引號的才可能是。
            if (name.Kind != SqlTokenKind.Identifier ||
                name.Start >= position ||
                (!name.IsQuoted && SqlKeywordCatalog.IsKeyword(name.Value)))
            {
                continue;
            }

            return name.Value;
        }

        return null;
    }

    /// <summary>沒有現成詞元時的多載。</summary>
    public static string? FindLast(string sql, int position)
    {
        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        if (position < 0 || position > sql.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        return FindLast(SqlTokenizer.Tokenize(sql, 0, position), position);
    }

    private static bool CanPrecedeStatement(SqlToken previous)
    {
        if (previous.Kind == SqlTokenKind.Operator)
        {
            return false;
        }

        return !previous.IsPunctuation(".") &&
            !previous.IsPunctuation("(") &&
            !previous.IsPunctuation(",");
    }
}
