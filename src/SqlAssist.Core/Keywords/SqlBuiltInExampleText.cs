using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 把一份內建說明的多段範例接成一份文字，供浮動預覽的範例分頁直接顯示。
/// </summary>
/// <remarks>
/// 只看 <see cref="SqlBuiltInExample"/> 的資料就決定得了的文字組法，因此放在 Core：
/// Ssms22 的 <c>SqlStructurePreviewControl</c> 只管把結果丟進同一個唯讀檢視，不必自己
/// 判斷「第幾段」「要不要空行」。每段前面一律加 <c>-- ▸ {title}</c> 當標頭——只有一段時
/// 也一樣，單一規則比「只有一段就不寫標頭」的補丁好守；段落之間空一行，讀起來像
/// 好幾個獨立的批次。
/// </remarks>
public static class SqlBuiltInExampleText
{
    /// <summary>每一段標頭的字首；沿用 T-SQL 註解語法，貼進查詢視窗不必先刪掉這一行。</summary>
    private const string HeaderPrefix = "-- ▸ ";

    /// <summary>
    /// 接成一份可以直接顯示的文字；沒有範例時回傳空字串。
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
            if (index > 0)
            {
                builder.Append('\n').Append('\n');
            }

            builder.Append(HeaderPrefix).Append(examples[index].Title).Append('\n');
            builder.Append(examples[index].Sql);
        }

        return builder.ToString();
    }
}
