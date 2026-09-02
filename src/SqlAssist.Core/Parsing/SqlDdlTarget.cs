using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 分辨 <c>ON</c> 後面接的是資料表還是述詞。
/// </summary>
/// <remarks>
/// <c>ON</c> 在 T-SQL 裡是兩件完全不同的事：<c>JOIN b ON b.x = a.x</c> 之後是述詞
/// （要的是欄位），<c>CREATE INDEX ix ON dbo.Lib_Reader (…)</c> 之後是資料表。
/// 分不出來的代價看得見——索引片段 <c>cix</c> 的資料表格從來沒有清單，而它的欄位格
/// 列出來的是整個資料庫的資料表與預存程序，因為範圍分析根本不知道那裡有一張表。
///
/// 判斷刻意<b>只看 <c>ON</c> 前面那兩個名稱單位</b>，不往回走到敘述開頭：
/// 「名稱之後是 <c>INDEX</c>／<c>STATISTICS</c>／<c>TRIGGER</c>」這個形狀在 T-SQL 裡
/// 只有 DDL 寫得出來，而往回走到敘述開頭要多認一整套邊界，還會把
/// <c>CREATE INDEX … (a) ON [PRIMARY]</c> 的檔案群組一起收進來——那個 <c>ON</c>
/// 前面是右括號，這條規則自己就擋掉了。
///
/// 這份判斷有兩個呼叫端（建議目標與範圍分析），各寫一份的症狀是其中一份改了另一份
/// 沒改：清單列得出資料表、欄位卻一個都沒有，或者反過來。
/// </remarks>
public static class SqlDdlTarget
{
    /// <summary><c>ON</c> 之前那個名稱屬於哪幾種物件時，<c>ON</c> 後面是資料表。</summary>
    /// <remarks>
    /// <c>CREATE</c>、<c>ALTER</c>、<c>DROP</c> 三種動詞不必比：這三個字都只是
    /// 再往前一格，而漏掉任何一種（<c>CREATE OR ALTER TRIGGER</c>、
    /// <c>ALTER INDEX ALL ON t</c>）的症狀都是那個位置安靜地沒有清單。
    /// 名稱後面接 <c>ON</c> 的物件只有這三種，動詞比不比對都得到同一個答案。
    /// </remarks>
    private static readonly HashSet<string> ObjectKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "INDEX", "STATISTICS", "TRIGGER"
        };

    /// <summary>
    /// <paramref name="index"/> 這個 <c>ON</c> 後面接的是資料表。
    /// </summary>
    public static bool IsDataSourceOn(IReadOnlyList<SqlToken> tokens, int index)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if (index < 2 || index >= tokens.Count || !tokens[index].IsKeyword("ON"))
        {
            return false;
        }

        // ON 前面必須是一個名稱單位。CREATE INDEX … (a) ON [PRIMARY] 的檔案群組
        // 前面是右括號，在這裡就被擋掉了。
        var nameEnd = index - 1;

        if (tokens[nameEnd].Kind != SqlTokenKind.Identifier)
        {
            return false;
        }

        var nameStart = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, nameEnd);

        return nameStart >= 1 &&
               tokens[nameStart - 1].Kind == SqlTokenKind.Identifier &&
               !tokens[nameStart - 1].IsQuoted &&
               ObjectKeywords.Contains(tokens[nameStart - 1].Value);
    }

    /// <summary>
    /// 詞元串流的尾巴是一個「後面接資料表」的 <c>ON</c>；找不到時回傳 -1。
    /// </summary>
    /// <remarks>
    /// 給游標前文用的：串流結束在游標正在輸入的那個詞元<b>之前</b>，
    /// 因此 <c>ON |</c> 的最後一個詞元就是 <c>ON</c>。使用者已經打出限定字時
    /// （<c>ON dbo.|</c>）尾巴多了名稱與點號，那幾個詞元是同一個名稱的一部分，
    /// 跳過它們問的仍然是同一個 <c>ON</c>。
    ///
    /// 跳的只有識別字與點號，所以 <c>JOIN b ON b.x = a.x </c> 這種寫完的述詞停在
    /// 等號上，不會誤認成 <c>ON</c>。
    /// </remarks>
    public static int FindTrailingDataSourceOn(IReadOnlyList<SqlToken> tokens)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        var index = tokens.Count - 1;

        while (index >= 0 &&
               !tokens[index].IsKeyword("ON") &&
               (tokens[index].IsPunctuation(".") || tokens[index].Kind == SqlTokenKind.Identifier))
        {
            index--;
        }

        return index >= 0 && IsDataSourceOn(tokens, index) ? index : -1;
    }
}
