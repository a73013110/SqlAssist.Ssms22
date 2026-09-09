using System;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Snippets;

/// <summary>包夾的替換範圍與內容；只讀文字，不依賴編輯器選取方向。</summary>
public sealed class SqlSnippetSurroundSelection
{
    private SqlSnippetSurroundSelection(int start, int end, string text, string baseIndent, bool expanded)
    {
        Start = start;
        Length = end - start;
        Text = text;
        BaseIndent = baseIndent;
        ExpandedToLines = expanded;
    }

    public int Start { get; }
    public int Length { get; }
    public string Text { get; }
    public string BaseIndent { get; }
    public bool ExpandedToLines { get; }

    public static SqlSnippetSurroundSelection Resolve(ISqlTextSource source, int start, int length)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (start < 0 || length <= 0 || start > source.Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var end = start + length;
        var first = start;
        while (first > 0 && !IsNewLine(source[first - 1]))
        {
            first--;
        }

        var firstEnd = start;
        while (firstEnd < source.Length && !IsNewLine(source[firstEnd]))
        {
            firstEnd++;
        }

        var line = source.Substring(first, firstEnd - first);
        var indent = SqlSnippetIndentation.LeadingWhitespace(line, line.Length);
        var multiline = end > firstEnd;
        // 完整單行也保留插入點的縮排，否則選到行首時 BEGIN 會向左跳、內容反而多縮一層。
        var wholeLine = start <= first + indent.Length && end == firstEnd && indent.Length < line.Length;
        if (!multiline && !wholeLine)
        {
            return new SqlSnippetSurroundSelection(start, end, source.Substring(start, length),
                SqlSnippetIndentation.LeadingWhitespace(line, start - first), false);
        }

        // 選到下一行第 0 欄（含 CRLF）不代表選到那一行，保留原有行尾在外框之後。
        var last = end;
        if (IsNewLine(source[last - 1]))
        {
            last--;
            if (last > first && source[last] == '\n' && source[last - 1] == '\r')
            {
                last--;
            }
        }
        else
        {
            while (last < source.Length && !IsNewLine(source[last]))
            {
                last++;
            }
        }

        var targetStart = first + (indent.Length == line.Length ? 0 : indent.Length);
        var textStart = last == firstEnd ? targetStart : first;
        return new SqlSnippetSurroundSelection(targetStart, last,
            source.Substring(textStart, last - textStart),
            SqlSnippetIndentation.LeadingWhitespace(line, targetStart - first),
            start > targetStart || last > end);
    }

    private static bool IsNewLine(char value) => value == '\r' || value == '\n';
}
