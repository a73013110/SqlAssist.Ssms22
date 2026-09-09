using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Snippets;

/// <summary>原生格式化、游標模式與預覽共用的插入點縮排。</summary>
public static class SqlSnippetIndentation
{
    /// <summary>只取插入點之前的前導空白，避免游標在縮排中間時把後續行推得更深。</summary>
    public static string LeadingWhitespace(string line, int before)
    {
        var length = 0;
        while (length < line.Length && length < before && (line[length] == ' ' || line[length] == '\t'))
        {
            length++;
        }

        return line.Substring(0, length);
    }

    /// <summary>第一行已有緩衝區的縮排；只補後續非空行，並視 CRLF 為一次換行。</summary>
    public static IEnumerable<int> ContinuationStarts(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\r' && text[index] != '\n')
            {
                continue;
            }

            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            var next = index + 1;
            if (next < text.Length && text[next] != '\r' && text[next] != '\n')
            {
                yield return next;
            }
        }
    }

    public static string Apply(string text, string indent, ref int caretOffset)
    {
        var offsets = new[] { caretOffset };
        var indented = Apply(text, indent, offsets);
        caretOffset = offsets[0];
        return indented;
    }

    /// <summary>同時把多個位置搬到補完縮排之後；負數（沒有這個位置）維持原樣。</summary>
    /// <remarks>
    /// 預覽要標出包夾內容的範圍，需要的是<b>同一次</b>縮排下的起訖點。分兩次套用會得到
    /// 兩份字串，而第二份的位置對不上第一份——位置與文字必須是同一次計算的結果。
    /// </remarks>
    public static string Apply(string text, string indent, int[] offsets)
    {
        if (indent.Length == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var start = 0;
        var original = (int[])offsets.Clone();
        foreach (var position in ContinuationStarts(text))
        {
            builder.Append(text, start, position - start).Append(indent);
            start = position;
            for (var index = 0; index < offsets.Length; index++)
            {
                // 補在位置之前的縮排才推得動它；行首正好是起點時，縮排留在範圍外面。
                if (position <= original[index])
                {
                    offsets[index] += indent.Length;
                }
            }
        }

        return builder.Append(text, start, text.Length - start).ToString();
    }
}
