using System;
using System.Text;

namespace SqlAssist.Core.Parsing;

/// <summary>結構、邊欄與上下文提示共用的有界文字，不保留或複製整個大區塊。</summary>
public static class BlockContextText
{
    public const int MaximumSummaryLength = 160;

    public static string KindName(BlockKind kind) => kind switch
    {
        BlockKind.Try => "BEGIN TRY", BlockKind.Catch => "BEGIN CATCH", BlockKind.Case => "CASE",
        BlockKind.Parenthesis => "( )", BlockKind.Bracket => "[ ]", _ => "BEGIN"
    };

    public static string Summarize(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var result = new StringBuilder();
        foreach (var value in text)
        {
            if (result.Length >= MaximumSummaryLength)
            {
                // UTF-16 摘要不能截在代理對中間，否則 SQL 中的補充平面字元會變成亂碼。
                if (char.IsHighSurrogate(result[result.Length - 1])) result.Length--;
                result.Append('…');
                break;
            }
            if (char.IsWhiteSpace(value) || char.IsControl(value))
            {
                if (result.Length > 0 && result[result.Length - 1] != ' ') result.Append(' ');
            }
            else result.Append(value);
        }
        // 呼叫端只讀有限長度的行前綴，輸入本身也可能剛好截在代理對中間。
        while (result.Length > 0 && result[result.Length - 1] == ' ') result.Length--;
        if (result.Length > 0 && char.IsHighSurrogate(result[result.Length - 1]))
        {
            result.Length--;
            result.Append('…');
        }
        return result.ToString();
    }

    public static string Format(BlockKind kind, int oneBasedLine, string openingLine, string precedingLine)
    {
        if (oneBasedLine < 1) throw new ArgumentOutOfRangeException(nameof(oneBasedLine));
        var summary = Summarize(openingLine);
        // BEGIN 自成一行時帶上控制它的前一行，讓 IF／WHILE 的條件仍可辨認。
        if (string.Equals(summary, KindName(kind), StringComparison.OrdinalIgnoreCase))
            summary = Summarize(precedingLine);
        return $"↑ {KindName(kind)}（第 {oneBasedLine} 行） {summary}".TrimEnd();
    }
}
