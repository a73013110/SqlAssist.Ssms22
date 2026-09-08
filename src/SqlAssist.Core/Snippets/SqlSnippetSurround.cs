using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Snippets;

/// <summary>
/// 把選取的文字接進樣板的包夾欄位之前，先把它的縮排整理好。
/// </summary>
/// <remarks>
/// 縮排要分三段看，缺任何一段都會歪：
///
/// <list type="number">
/// <item>選取文字自己的<b>相對</b>縮排（巢狀的 <c>IF</c> 內層要比外層深）——保留。</item>
/// <item>選取文字原本的<b>共同</b>縮排——去掉，否則包一層就多推一層，包三次就跑到畫面外。</item>
/// <item>樣板在錨點那一格的縮排——補上，因為引擎只會把第一行放在錨點的位置，
///   第 2 行起一律從第 0 欄開始。</item>
/// </list>
///
/// 整段插入點的<b>基準</b>縮排不在這裡：那一份由
/// <c>SqlSnippetExpansionController.FormatSpan</c> 在插入之後補，而它補的是
/// 「插入點所在行的前導空白」，與包夾與否無關。兩者相加才是最後看到的縮排。
/// </remarks>
public static class SqlSnippetSurround
{
    /// <summary>
    /// 去掉選取文字的共同縮排，再把第 2 行起對齊到錨點。
    /// </summary>
    /// <param name="selectedText">使用者選取的原文；換行格式不拘。</param>
    /// <param name="anchorIndent">樣板中包夾欄位前面的空白。</param>
    /// <remarks>
    /// 換行一律先正規化成 <c>\n</c>：樣板的 <c>Code</c> 用的就是 <c>\n</c>，而寫回
    /// 編輯器那一刻的換行由 <see cref="SqlSnippetExpansion.GetText"/> 依快照統一，
    /// 中途多一種換行只會讓那一步少換到幾行。
    ///
    /// 只有一行時原樣返回：沒有第 2 行要對齊，而去掉那一行的前導空白反而會把
    /// 「從行中間選一段」的結果往左推。
    /// </remarks>
    public static string Reindent(string? selectedText, string? anchorIndent)
    {
        if (string.IsNullOrEmpty(selectedText))
        {
            return string.Empty;
        }

        var lines = SplitLines(selectedText!);

        if (lines.Count == 1)
        {
            return lines[0];
        }

        var common = CommonIndent(lines);
        var indent = anchorIndent ?? string.Empty;
        var builder = new StringBuilder(selectedText!.Length + (lines.Count * indent.Length));

        for (var index = 0; index < lines.Count; index++)
        {
            var line = Dedent(lines[index], common);

            if (index > 0)
            {
                builder.Append('\n');

                // 空白行不補縮排：那只會變成一行看不見的尾隨空白，
                // 而下一次存檔又會被編輯器刪掉，diff 多出無意義的變動。
                if (line.Length > 0)
                {
                    builder.Append(indent);
                }
            }

            builder.Append(line);
        }

        return builder.ToString();
    }

    private static List<string> SplitLines(string value)
    {
        var lines = new List<string>();
        var start = 0;

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\r' && value[index] != '\n')
            {
                continue;
            }

            lines.Add(value.Substring(start, index - start));

            if (value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        lines.Add(value.Substring(start));
        return lines;
    }

    /// <summary>
    /// 每一行共同的前導空白。
    /// </summary>
    /// <remarks>
    /// 比的是<b>字元</b>而不是換算後的欄數：使用者可能混用 Tab 與空白，而換算需要
    /// 一個 Tab 寬度的設定值——猜錯的症狀是包夾之後整段偏移，卻沒有任何錯誤。
    /// 逐字元取最長共同前綴時，混用的那幾行自然只會共到相同的那一段。
    ///
    /// 全是空白的行不參與：它們的長度是使用者沒有在意過的尾隨空白，
    /// 讓它決定共同縮排就會把有內容的行少去掉一截。
    /// </remarks>
    private static string CommonIndent(IReadOnlyList<string> lines)
    {
        string? common = null;

        foreach (var line in lines)
        {
            var length = 0;

            while (length < line.Length && IsIndent(line[length]))
            {
                length++;
            }

            if (length == line.Length)
            {
                continue;
            }

            if (common is null)
            {
                common = line.Substring(0, length);
                continue;
            }

            var shared = 0;

            while (shared < common.Length && shared < length && common[shared] == line[shared])
            {
                shared++;
            }

            common = common.Substring(0, shared);

            if (common.Length == 0)
            {
                break;
            }
        }

        return common ?? string.Empty;
    }

    private static string Dedent(string line, string common)
    {
        if (IsBlank(line))
        {
            return string.Empty;
        }

        return line.StartsWith(common, StringComparison.Ordinal)
            ? line.Substring(common.Length)
            : line;
    }

    private static bool IsBlank(string line)
    {
        foreach (var character in line)
        {
            if (!IsIndent(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIndent(char value) => value == ' ' || value == '\t';
}
