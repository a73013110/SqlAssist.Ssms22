using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SqlAssist.Core.Json;

/// <summary>
/// 縮排的 JSON 輸出。
/// </summary>
/// <remarks>
/// 寫出去的是嚴格的 JSON——<see cref="JsonReader"/> 接受註解與尾隨逗號，
/// 但那是為了容忍使用者手改的檔案，不是我們自己該產生的東西。
///
/// 以「呼叫端決定順序」的方式收成員，而不是接一個字典：這份檔案使用者看得到，
/// shortcut 排在 code 前面比字典的雜湊順序好讀，而且順序穩定，
/// 每次存檔的差異才只會出現在真的有改的地方。
/// </remarks>
public sealed class JsonWriter
{
    private readonly StringBuilder _builder = new();

    /// <summary>每一層物件「下一個成員是不是第一個」；用來決定要不要先寫逗號。</summary>
    private readonly Stack<bool> _pendingFirstMember = new();

    private int _depth;

    private JsonWriter()
    {
    }

    /// <summary>產生一份 JSON 文件。</summary>
    public static string Write(Action<JsonWriter> body)
    {
        if (body is null)
        {
            throw new ArgumentNullException(nameof(body));
        }

        var writer = new JsonWriter();
        body(writer);
        writer._builder.Append('\n');
        return writer._builder.ToString();
    }

    /// <summary>寫一個物件；在 <paramref name="body"/> 裡呼叫 <c>Member</c> 加成員。</summary>
    public void Object(Action<JsonWriter> body)
    {
        _builder.Append('{');
        _depth++;
        _pendingFirstMember.Push(true);

        body(this);

        var wroteNothing = _pendingFirstMember.Pop();
        _depth--;

        if (!wroteNothing)
        {
            AppendNewLineIndent();
        }

        _builder.Append('}');
    }

    /// <summary>寫一個值為物件或陣列的成員。</summary>
    public void Member(string name, Action<JsonWriter> body)
    {
        StartMember(name);
        body(this);
    }

    public void Member(string name, string value)
    {
        StartMember(name);
        AppendString(value);
    }

    public void Member(string name, int value)
    {
        StartMember(name);
        _builder.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    public void Member(string name, bool value)
    {
        StartMember(name);
        _builder.Append(value ? "true" : "false");
    }

    /// <summary>寫一個陣列，每個元素由 <paramref name="writeItem"/> 產生。</summary>
    public void Array<T>(IEnumerable<T> items, Action<JsonWriter, T> writeItem)
    {
        _builder.Append('[');
        _depth++;
        var wroteAny = false;

        foreach (var item in items)
        {
            if (wroteAny)
            {
                _builder.Append(',');
            }

            AppendNewLineIndent();
            writeItem(this, item);
            wroteAny = true;
        }

        _depth--;

        if (wroteAny)
        {
            AppendNewLineIndent();
        }

        _builder.Append(']');
    }

    /// <summary>寫一個字串值；陣列元素用得到。</summary>
    public void Value(string value) => AppendString(value);

    private void StartMember(string name)
    {
        if (_pendingFirstMember.Count > 0)
        {
            if (!_pendingFirstMember.Pop())
            {
                _builder.Append(',');
            }

            _pendingFirstMember.Push(false);
        }

        AppendNewLineIndent();
        AppendString(name);
        _builder.Append(": ");
    }

    private void AppendNewLineIndent()
    {
        _builder.Append('\n');
        _builder.Append(' ', _depth * 2);
    }

    private void AppendString(string? value)
    {
        _builder.Append('"');

        foreach (var current in value ?? string.Empty)
        {
            switch (current)
            {
                case '"': _builder.Append("\\\""); break;
                case '\\': _builder.Append("\\\\"); break;
                case '\b': _builder.Append("\\b"); break;
                case '\f': _builder.Append("\\f"); break;
                case '\n': _builder.Append("\\n"); break;
                case '\r': _builder.Append("\\r"); break;
                case '\t': _builder.Append("\\t"); break;

                default:
                    // 控制字元必須跳脫；其餘一律原樣寫出，檔案是 UTF-8，
                    // 中文描述沒有理由變成一串 \uXXXX。
                    if (current < ' ')
                    {
                        _builder.Append("\\u");
                        _builder.Append(((int)current).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _builder.Append(current);
                    }

                    break;
            }
        }

        _builder.Append('"');
    }
}
