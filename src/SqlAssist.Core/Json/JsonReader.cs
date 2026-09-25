using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SqlAssist.Core.Json;

/// <summary>讀不成 JSON 的原因。</summary>
public enum JsonParseError
{
    TrailingContent,
    UnexpectedEnd,
    ObjectNotClosed,
    MemberNameNotString,
    ColonExpected,
    MemberSeparatorExpected,
    ArrayNotClosed,
    ElementSeparatorExpected,
    StringNotClosed,
    EscapeIncomplete,
    UnicodeEscapeInvalid,
    UnknownEscape,
    UnknownValue,
    CommentNotClosed,
}

/// <summary>讀不成 JSON。</summary>
/// <remarks>
/// 訊息會出現在片段管理員的狀態列，所以要跟著介面語言；譯文由 Core 的
/// <c>JsonParseException.Text.cs</c> 補上。<c>tools/SqlAssist.TextGenerator</c> 也編這一份檔案來讀
/// <c>.resjson</c>，那裡沒有譯文可用（譯文正是它產生的），訊息就退回錯誤碼與位置。
/// </remarks>
public sealed partial class JsonParseException : Exception
{
    public JsonParseException(JsonParseError error, int position, string? detail = null)
        : base(Describe(error, position, detail))
    {
        Error = error;
        Position = position;
    }

    public JsonParseError Error { get; }

    /// <summary>出錯的字元位移，供呼叫端指回檔案裡的位置。</summary>
    public int Position { get; }

    private static string Describe(JsonParseError error, int position, string? detail)
    {
        var message = detail is null ? $"{error} @ {position}" : $"{error} '{detail}' @ {position}";
        Localize(error, position, detail, ref message);
        return message;
    }

    static partial void Localize(JsonParseError error, int position, string? detail, ref string message);
}

/// <summary>
/// 最小的 JSON 剖析器。
/// </summary>
/// <remarks>
/// 支援 RFC 8259 的全部語法，外加兩項對「使用者會自己編輯的設定檔」很划算的寬容：
/// 允許 <c>//</c> 與 <c>/* */</c> 註解，以及物件與陣列的尾隨逗號。
/// 兩者都只是接受更多輸入，寫出去的內容仍然是嚴格的 JSON。
/// </remarks>
public static class JsonReader
{
    /// <summary>剖析一份 JSON 文件。</summary>
    /// <exception cref="JsonParseException">內容不是合法的 JSON。</exception>
    public static JsonValue Parse(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var position = 0;

        // UTF-8 BOM 以 ﻿ 進到字串裡，剖析器看到的是一個不合法的字元。
        if (text.Length > 0 && text[0] == '﻿')
        {
            position = 1;
        }

        var value = ParseValue(text, ref position);
        SkipTrivia(text, ref position);

        if (position < text.Length)
        {
            throw new JsonParseException(JsonParseError.TrailingContent, position);
        }

        return value;
    }

    private static JsonValue ParseValue(string text, ref int position)
    {
        SkipTrivia(text, ref position);

        if (position >= text.Length)
        {
            throw new JsonParseException(JsonParseError.UnexpectedEnd, position);
        }

        return text[position] switch
        {
            '{' => ParseObject(text, ref position),
            '[' => ParseArray(text, ref position),
            '"' => JsonValue.FromString(ParseString(text, ref position)),
            't' => ParseLiteral(text, ref position, "true", JsonValue.FromBoolean(true)),
            'f' => ParseLiteral(text, ref position, "false", JsonValue.FromBoolean(false)),
            'n' => ParseLiteral(text, ref position, "null", JsonValue.Null),
            _ => ParseNumber(text, ref position)
        };
    }

    private static JsonValue ParseObject(string text, ref int position)
    {
        position++;
        var members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);

        while (true)
        {
            SkipTrivia(text, ref position);

            if (position >= text.Length)
            {
                throw new JsonParseException(JsonParseError.ObjectNotClosed, position);
            }

            if (text[position] == '}')
            {
                position++;
                return JsonValue.FromObject(members);
            }

            if (text[position] != '"')
            {
                throw new JsonParseException(JsonParseError.MemberNameNotString, position);
            }

            var name = ParseString(text, ref position);
            SkipTrivia(text, ref position);

            if (position >= text.Length || text[position] != ':')
            {
                throw new JsonParseException(JsonParseError.ColonExpected, position);
            }

            position++;
            members[name] = ParseValue(text, ref position);
            SkipTrivia(text, ref position);

            if (position < text.Length && text[position] == ',')
            {
                position++;
                continue;
            }

            if (position < text.Length && text[position] == '}')
            {
                position++;
                return JsonValue.FromObject(members);
            }

            throw new JsonParseException(JsonParseError.MemberSeparatorExpected, position);
        }
    }

    private static JsonValue ParseArray(string text, ref int position)
    {
        position++;
        var items = new List<JsonValue>();

        while (true)
        {
            SkipTrivia(text, ref position);

            if (position >= text.Length)
            {
                throw new JsonParseException(JsonParseError.ArrayNotClosed, position);
            }

            if (text[position] == ']')
            {
                position++;
                return JsonValue.FromArray(items);
            }

            items.Add(ParseValue(text, ref position));
            SkipTrivia(text, ref position);

            if (position < text.Length && text[position] == ',')
            {
                position++;
                continue;
            }

            if (position < text.Length && text[position] == ']')
            {
                position++;
                return JsonValue.FromArray(items);
            }

            throw new JsonParseException(JsonParseError.ElementSeparatorExpected, position);
        }
    }

    private static string ParseString(string text, ref int position)
    {
        // 進來時 text[position] 必定是開頭的引號。
        position++;
        var builder = new StringBuilder();

        while (true)
        {
            if (position >= text.Length)
            {
                throw new JsonParseException(JsonParseError.StringNotClosed, position);
            }

            var current = text[position];

            if (current == '"')
            {
                position++;
                return builder.ToString();
            }

            if (current != '\\')
            {
                builder.Append(current);
                position++;
                continue;
            }

            position++;

            if (position >= text.Length)
            {
                throw new JsonParseException(JsonParseError.EscapeIncomplete, position);
            }

            var escape = text[position];
            position++;

            switch (escape)
            {
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case '/': builder.Append('/'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;

                case 'u':
                    if (position + 4 > text.Length ||
                        !ushort.TryParse(
                            text.Substring(position, 4),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out var code))
                    {
                        throw new JsonParseException(JsonParseError.UnicodeEscapeInvalid, position);
                    }

                    builder.Append((char)code);
                    position += 4;
                    break;

                default:
                    throw new JsonParseException(JsonParseError.UnknownEscape, position - 1, escape.ToString());
            }
        }
    }

    private static JsonValue ParseNumber(string text, ref int position)
    {
        var start = position;

        if (position < text.Length && (text[position] == '-' || text[position] == '+'))
        {
            position++;
        }

        while (position < text.Length &&
               (char.IsDigit(text[position]) ||
                text[position] == '.' ||
                text[position] == 'e' ||
                text[position] == 'E' ||
                ((text[position] == '-' || text[position] == '+') &&
                 (text[position - 1] == 'e' || text[position - 1] == 'E'))))
        {
            position++;
        }

        var literal = text.Substring(start, position - start);

        if (!double.TryParse(
                literal,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number))
        {
            throw new JsonParseException(JsonParseError.UnknownValue, start, literal);
        }

        return JsonValue.FromNumber(number);
    }

    private static JsonValue ParseLiteral(string text, ref int position, string literal, JsonValue value)
    {
        if (position + literal.Length > text.Length ||
            string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0)
        {
            throw new JsonParseException(JsonParseError.UnknownValue, position);
        }

        position += literal.Length;
        return value;
    }

    /// <summary>略過空白與註解。</summary>
    private static void SkipTrivia(string text, ref int position)
    {
        while (position < text.Length)
        {
            var current = text[position];

            if (char.IsWhiteSpace(current))
            {
                position++;
                continue;
            }

            if (current != '/' || position + 1 >= text.Length)
            {
                return;
            }

            var next = text[position + 1];

            if (next == '/')
            {
                position += 2;

                while (position < text.Length && text[position] != '\n')
                {
                    position++;
                }

                continue;
            }

            if (next == '*')
            {
                position += 2;

                while (position + 1 < text.Length &&
                       !(text[position] == '*' && text[position + 1] == '/'))
                {
                    position++;
                }

                if (position + 1 >= text.Length)
                {
                    throw new JsonParseException(JsonParseError.CommentNotClosed, position);
                }

                position += 2;
                continue;
            }

            return;
        }
    }
}
