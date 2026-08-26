using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core;

/// <summary>
/// 把展開後的欄位排成要寫進編輯器的那一段文字。
/// </summary>
/// <remarks>
/// 欄位名稱本身（限定字、方括號）由呼叫端決定，這裡只管排版，因此完全可以單元測試。
///
/// 一行放得下就放一行——那是絕大多數情形，也是使用者按下 Tab 時預期看到的結果。
/// 放不下才換行，並對齊原本 <c>*</c> 的位置：一張一百多欄的資料表攤成一行，
/// 等於逼使用者橫向捲動去讀自己剛剛產生的東西。
/// </remarks>
public static class SqlWildcardExpansionText
{
    /// <param name="columns">已經加好限定字與括號的欄位名稱。</param>
    /// <param name="indent">
    /// 換行後每一行的前導文字，通常是原本 <c>*</c> 之前那一段改成的空白；
    /// 它的長度同時也是第一行的起始欄位。
    /// </param>
    /// <param name="maximumWidth">超過這個寬度就換行。</param>
    /// <param name="newLine">緩衝區使用的換行字元。</param>
    public static string Build(
        IReadOnlyList<string> columns,
        string indent,
        int maximumWidth,
        string newLine)
    {
        if (columns is null)
        {
            throw new ArgumentNullException(nameof(columns));
        }

        if (columns.Count == 0)
        {
            return string.Empty;
        }

        indent ??= string.Empty;
        newLine = string.IsNullOrEmpty(newLine) ? "\r\n" : newLine;

        var singleLine = string.Join(", ", columns);

        if (indent.Length + singleLine.Length <= maximumWidth)
        {
            return singleLine;
        }

        var builder = new StringBuilder(singleLine.Length + (columns.Count * indent.Length));
        var width = indent.Length;

        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            var separator = index == columns.Count - 1 ? string.Empty : ",";

            // 第一個欄位一定留在原地：它接在 SELECT 後面，把它推到下一行
            // 只會讓 SELECT 孤零零地留在上一行。
            if (index > 0)
            {
                if (width + 1 + column.Length + separator.Length > maximumWidth)
                {
                    builder.Append(newLine).Append(indent);
                    width = indent.Length;
                }
                else
                {
                    builder.Append(' ');
                    width++;
                }
            }

            builder.Append(column).Append(separator);
            width += column.Length + separator.Length;
        }

        return builder.ToString();
    }

    /// <summary>
    /// 由 <c>*</c> 之前的那一段行內文字算出換行時要用的前導空白。
    /// </summary>
    /// <remarks>
    /// 定位字元原樣保留、其餘一律換成空白：定位字元換成空白會讓對齊在
    /// 定位寬度不是 4 的機器上跑掉，而把程式碼原樣抄過來則會在下一行
    /// 留下一段看不出來的重複文字。
    /// </remarks>
    public static string BuildIndent(string linePrefix)
    {
        if (string.IsNullOrEmpty(linePrefix))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(linePrefix.Length);

        foreach (var character in linePrefix)
        {
            builder.Append(character == '\t' ? '\t' : ' ');
        }

        return builder.ToString();
    }
}
