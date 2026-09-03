using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 找出文字中某個位置所在的識別字，供滑鼠停留提示與物件解析使用。
/// </summary>
public static class SqlIdentifierScanner
{
    /// <summary>
    /// 取得 <paramref name="position"/> 所在的識別字；該位置不在識別字上、
    /// 或位於字串與註解之中時回傳 null。
    /// </summary>
    public static SqlIdentifierReference? FindAt(string text, int position)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (position < 0 || position > text.Length)
        {
            return null;
        }

        // 方括號識別字裡放的是物件名稱，必須與字串、註解分開判斷。
        var state = SqlLexicalContext.GetState(text, position);

        if (state == SqlLexicalState.String ||
            state == SqlLexicalState.LineComment ||
            state == SqlLexicalState.BlockComment)
        {
            return null;
        }

        if (!TryReadIdentifierAt(text, position, out var name, out var start, out var end))
        {
            return null;
        }

        // 一路往左吃限定字，不是只吃一段：只吃一段的話
        // [192.0.2.10].[LibArchive].[dbo].[Loan] 會被讀成 dbo.Loan，
        // 而 F12 會跳到目前連線裡同名的那一個物件——跳錯地方比跳不動糟。
        //
        // 讀到上限之後還有下一段，代表這個名稱段數過多。那時不留下路徑，
        // 讓下游明確地查不到，而不是拿最後四段去猜。
        var parts = new List<string> { name };
        var referenceStart = start;
        var cursor = start;

        while (TryReadQualifierBefore(text, cursor, out var qualifierStart))
        {
            parts.Insert(0, Unquote(TrimTrailingDot(
                text.Substring(qualifierStart, cursor - qualifierStart))));
            referenceStart = qualifierStart;
            cursor = qualifierStart;

            if (parts.Count > SqlObjectPath.MaximumNameParts)
            {
                break;
            }
        }

        SqlObjectPath.TryParseName(parts, out var path);
        return new SqlIdentifierReference(name, path, referenceStart, end - referenceStart);
    }

    /// <summary>讀出位置所在的識別字，支援方括號與雙引號括住的形式。</summary>
    private static bool TryReadIdentifierAt(
        string text,
        int position,
        out string name,
        out int start,
        out int end)
    {
        name = string.Empty;
        start = 0;
        end = 0;

        if (TryReadQuotedIdentifierAt(text, position, '[', ']', out name, out start, out end))
        {
            return true;
        }

        if (TryReadQuotedIdentifierAt(text, position, '"', '"', out name, out start, out end))
        {
            return true;
        }

        // 游標剛好停在識別字右側邊界時，仍應視為指向該識別字。
        var probe = position;

        if (probe > 0 && (probe == text.Length || !IsIdentifierCharacter(text[probe])))
        {
            probe--;
        }

        if (probe < 0 || probe >= text.Length || !IsIdentifierCharacter(text[probe]))
        {
            return false;
        }

        start = probe;

        while (start > 0 && IsIdentifierCharacter(text[start - 1]))
        {
            start--;
        }

        end = probe + 1;

        while (end < text.Length && IsIdentifierCharacter(text[end]))
        {
            end++;
        }

        name = text.Substring(start, end - start);
        return name.Length > 0;
    }

    /// <summary>
    /// 由行首往後掃描括住的識別字，判斷 <paramref name="position"/> 是否落在其中。
    /// </summary>
    /// <remarks>
    /// 必須由前往後掃：<c>]]</c> 與 <c>""</c> 是跳脫寫法而不是結尾，
    /// 往回掃無法區分「跳脫的右括號」與「上一個識別字的結尾」，
    /// 例如 <c>[Weird]]Name]</c> 往回掃會誤判成識別字 <c>Name</c>。
    /// </remarks>
    private static bool TryReadQuotedIdentifierAt(
        string text,
        int position,
        char opening,
        char closing,
        out string name,
        out int start,
        out int end)
    {
        name = string.Empty;
        start = 0;
        end = 0;

        // 識別字不跨行，掃描範圍限制在游標所在的那一行。
        var lineStart = position;

        while (lineStart > 0 && text[lineStart - 1] != '\n' && text[lineStart - 1] != '\r')
        {
            lineStart--;
        }

        var lineEnd = position;

        while (lineEnd < text.Length && text[lineEnd] != '\n' && text[lineEnd] != '\r')
        {
            lineEnd++;
        }

        var index = lineStart;

        while (index < lineEnd)
        {
            if (text[index] != opening)
            {
                index++;
                continue;
            }

            var closeIndex = FindClosing(text, index + 1, lineEnd, closing);

            if (closeIndex < 0)
            {
                return false; // 未閉合的括號，後面不可能再有完整的識別字。
            }

            if (position >= index && position <= closeIndex)
            {
                start = index;
                end = closeIndex + 1;
                name = text.Substring(index + 1, closeIndex - index - 1)
                    .Replace(new string(closing, 2), closing.ToString());
                return name.Length > 0;
            }

            index = closeIndex + 1;
        }

        return false;
    }

    /// <summary>找出結尾字元，成對出現的視為跳脫而非結尾。</summary>
    private static int FindClosing(string text, int from, int limit, char closing)
    {
        var index = from;

        while (index < limit)
        {
            if (text[index] != closing)
            {
                index++;
                continue;
            }

            if (index + 1 < limit && text[index + 1] == closing)
            {
                index += 2;
                continue;
            }

            return index;
        }

        return -1;
    }

    /// <summary>往前找 <c>限定詞.</c> 形式的前綴。</summary>
    private static bool TryReadQualifierBefore(string text, int identifierStart, out int qualifierStart)
    {
        qualifierStart = identifierStart;
        var index = identifierStart;

        while (index > 0 && IsHorizontalWhitespace(text[index - 1]))
        {
            index--;
        }

        if (index == 0 || text[index - 1] != '.')
        {
            return false;
        }

        index--; // 跳過點號。

        while (index > 0 && IsHorizontalWhitespace(text[index - 1]))
        {
            index--;
        }

        if (index == 0)
        {
            return false;
        }

        // 又一個點號，代表中間這一段省略了：LibArchive..Loan 少寫結構描述。
        // 回報一個零長度的段，右對齊時位置才對得回去。
        if (text[index - 1] == '.')
        {
            qualifierStart = index;
            return true;
        }

        if (text[index - 1] == ']' || text[index - 1] == '"')
        {
            var closing = text[index - 1];
            var opening = closing == ']' ? '[' : '"';
            var openIndex = text.LastIndexOf(opening, index - 2);

            if (openIndex < 0)
            {
                return false;
            }

            qualifierStart = openIndex;
            return true;
        }

        var start = index;

        while (start > 0 && IsIdentifierCharacter(text[start - 1]))
        {
            start--;
        }

        if (start == index)
        {
            return false;
        }

        qualifierStart = start;
        return true;
    }

    private static string TrimTrailingDot(string value)
    {
        var trimmed = value.TrimEnd();
        return trimmed.EndsWith(".", StringComparison.Ordinal)
            ? trimmed.Substring(0, trimmed.Length - 1).TrimEnd()
            : trimmed;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '[' && value[value.Length - 1] == ']')
        {
            return value.Substring(1, value.Length - 2).Replace("]]", "]");
        }

        if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
        {
            return value.Substring(1, value.Length - 2).Replace("\"\"", "\"");
        }

        return value;
    }

    private static bool IsIdentifierCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_' || value == '#' || value == '@' || value == '$';
    }

    private static bool IsHorizontalWhitespace(char value)
    {
        return value == ' ' || value == '\t';
    }
}
