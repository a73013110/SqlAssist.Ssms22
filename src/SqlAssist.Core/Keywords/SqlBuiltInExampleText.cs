using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 把一份內建說明的多段範例接成一份文字，供浮動預覽的範例分頁直接顯示。
/// </summary>
/// <remarks>
/// 只看 <see cref="SqlBuiltInExample"/> 的資料就決定得了的文字組法，因此放在 Core：
/// Ssms22 的 <c>SqlStructurePanel</c> 只管把結果丟進同一個唯讀檢視，不必自己
/// 判斷「第幾段」「要不要空行」「要不要插 GO」。每段前面一律加 <c>-- ▸ {title}</c> 當
/// 標頭——只有一段時也一樣，單一規則比「只有一段就不寫標頭」的補丁好守。段落之間插一行
/// <c>GO</c> 再空一行：整頁複製後要能直接執行，同一筆的多段常常各自宣告同名變數或暫存表，
/// 少了 <c>GO</c> 批次不分開，第二段的 <c>DECLARE</c> 會撞第一段。某段本身已經以
/// <c>GO</c> 結尾（例如示範建立預存程序需要獨立批次）時不重複加，避免變成連續兩行 <c>GO</c>；
/// 最後一段後面不加，那裡本來就是文字結尾。
/// </remarks>
public static class SqlBuiltInExampleText
{
    /// <summary>每一段標頭的字首；沿用 T-SQL 註解語法，貼進查詢視窗不必先刪掉這一行。</summary>
    private const string HeaderPrefix = "-- ▸ ";

    /// <summary>段落之間的批次分隔字；獨立一行，SSMS 才認得出是 GO。</summary>
    private const string BatchSeparator = "GO";

    /// <summary>
    /// 接成一份可以直接顯示、整頁複製後可以直接執行的文字；沒有範例時回傳空字串。
    /// </summary>
    public static string Combine(IReadOnlyList<SqlBuiltInExample> examples)
    {
        if (examples is null || examples.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        for (var index = 0; index < examples.Count; index++)
        {
            var example = examples[index];

            if (index > 0)
            {
                builder.Append('\n').Append('\n');
            }

            builder.Append(HeaderPrefix).Append(example.Title).Append('\n');
            builder.Append(example.Sql);

            var isLast = index == examples.Count - 1;

            if (!isLast && !EndsWithBatchSeparator(example.Sql))
            {
                builder.Append('\n').Append(BatchSeparator);
            }
        }

        return builder.ToString();
    }

    /// <summary>這一段的最後一行是不是已經自己寫了 GO——寫過就不再補一次。</summary>
    private static bool EndsWithBatchSeparator(string sql)
    {
        var trimmed = sql.TrimEnd();
        var lastNewline = trimmed.LastIndexOf('\n');
        var lastLine = lastNewline >= 0 ? trimmed.Substring(lastNewline + 1) : trimmed;

        return lastLine.TrimEnd('\r').Equals(BatchSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
