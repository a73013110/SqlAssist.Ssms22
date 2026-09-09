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
        if (indent.Length == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var start = 0;
        var originalCaret = caretOffset;
        foreach (var position in ContinuationStarts(text))
        {
            builder.Append(text, start, position - start).Append(indent);
            start = position;
            if (position <= originalCaret)
            {
                caretOffset += indent.Length;
            }
        }

        return builder.Append(text, start, text.Length - start).ToString();
    }
}
