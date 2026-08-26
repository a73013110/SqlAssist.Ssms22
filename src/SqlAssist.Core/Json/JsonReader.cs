using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SqlAssist.Core.Json;

/// <summary>讀不成 JSON。</summary>
public sealed class JsonParseException : Exception
{
    public JsonParseException(string message, int position)
        : base($"{message}（位置 {position}）")
    {
        Position = position;
    }

    /// <summary>出錯的字元位移，供呼叫端指回檔案裡的位置。</summary>
    public int Position { get; }
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
            throw new JsonParseException("文件結尾之後還有內容", position);
        }

        return value;
    }

    private static JsonValue ParseValue(string text, ref int position)
    {
        SkipTrivia(text, ref position);

        if (position >= text.Length)
        {
            throw new JsonParseException("內容意外結束", position);
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
                throw new JsonParseException("物件沒有結尾的 }", position);
            }

            if (text[position] == '}')
            {
                position++;
                return JsonValue.FromObject(members);
            }

            if (text[position] != '"')
            {
                throw new JsonParseException("物件的成員名稱必須是字串", position);
            }

            var name = ParseString(text, ref position);
            SkipTrivia(text, ref position);

            if (position >= text.Length || text[position] != ':')
            {
                throw new JsonParseException("成員名稱之後必須是 :", position);
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

            throw new JsonParseException("成員之後必須是 , 或 }", position);
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
                throw new JsonParseException("陣列沒有結尾的 ]", position);
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

            throw new JsonParseException("元素之後必須是 , 或 ]", position);
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
                throw new JsonParseException("字串沒有結尾的引號", position);
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
                throw new JsonParseException("跳脫序列沒有寫完", position);
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
                        throw new JsonParseException("\\u 之後必須是四位十六進位數字", position);
                    }

                    builder.Append((char)code);
                    position += 4;
                    break;

                default:
                    throw new JsonParseException($"認不得的跳脫字元 \\{escape}", position - 1);
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
            throw new JsonParseException($"認不得的值 {literal}", start);
        }

        return JsonValue.FromNumber(number);
    }

    private static JsonValue ParseLiteral(string text, ref int position, string literal, JsonValue value)
    {
        if (position + literal.Length > text.Length ||
            string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0)
        {
            throw new JsonParseException("認不得的值", position);
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
                    throw new JsonParseException("區塊註解沒有結尾的 */", position);
                }

                position += 2;
                continue;
            }

            return;
        }
    }
}
