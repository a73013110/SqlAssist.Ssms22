using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Snippets;

/// <summary>Snippet 經過一次剖析後，供一般插入與原生 Expansion 共用的結果。</summary>
public sealed class SqlSnippetExpansion
{
    private SqlSnippetExpansion(
        string text,
        string nativeCode,
        int caretOffset,
        IReadOnlyList<SqlSnippetPlaceholder> fields)
    {
        Text = text;
        NativeCode = nativeCode;
        CaretOffset = caretOffset;
        Fields = fields;
    }

    /// <summary>一般插入與原生失敗時使用的完整文字。</summary>
    public string Text { get; }

    /// <summary>
    /// 原生 Snippet XML 的 Code 內容。已知欄位與保留標記維持原樣，
    /// 其餘錢字號已依 VS Snippet 規則加倍。
    /// </summary>
    public string NativeCode { get; }

    public int CaretOffset { get; }

    /// <summary>
    /// 依程式碼<b>首次出現順序</b>排列的欄位，重複的只留第一次。
    /// </summary>
    /// <remarks>
    /// 這就是原生引擎的 Tab 導航順序。刻意不記每一次出現的位置：同名欄位的同步是
    /// 引擎自己用標記做的，留一份位置只會變成沒有人讀、卻看起來像同步機制的資料。
    /// </remarks>
    public IReadOnlyList<SqlSnippetPlaceholder> Fields { get; }

    public string GetText(string newLine, out int caretOffset)
    {
        caretOffset = NormalizeLineEndings(Text.Substring(0, CaretOffset), newLine).Length;
        return NormalizeLineEndings(Text, newLine);
    }

    public string GetNativeCode(string newLine) => NormalizeLineEndings(NativeCode, newLine);

    public static SqlSnippetExpansion Create(SqlSnippet snippet)
    {
        if (snippet is null)
        {
            throw new ArgumentNullException(nameof(snippet));
        }

        var placeholders = new Dictionary<string, SqlSnippetPlaceholder>(StringComparer.OrdinalIgnoreCase);

        foreach (var placeholder in snippet.Placeholders)
        {
            placeholders[placeholder.Id] = placeholder;
        }

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<SqlSnippetPlaceholder>(snippet.Placeholders.Count);
        var text = new StringBuilder(snippet.Code.Length);
        var native = new StringBuilder(snippet.Code.Length);
        var caretOffset = -1;
        var index = 0;

        while (index < snippet.Code.Length)
        {
            if (snippet.Code[index] != '$' ||
                !SqlSnippetPlaceholders.TryReadMarker(snippet.Code, index, out var id, out var end))
            {
                AppendLiteral(snippet.Code[index], text, native);
                index++;
                continue;
            }

            if (SqlSnippetPlaceholders.IsNamed(id, SqlSnippetPlaceholders.EndId))
            {
                if (caretOffset < 0)
                {
                    caretOffset = text.Length;
                    native.Append(SqlSnippet.CaretMarker);
                }
                else
                {
                    AppendLiteral(snippet.Code, index, end - index, text, native);
                }

                index = end;
                continue;
            }

            if (SqlSnippetPlaceholders.IsNamed(id, SqlSnippetPlaceholders.SelectedId))
            {
                // Completion 提交沒有選取文字；原生引擎仍保留官方的 $selected$ 語意。
                native.Append('$').Append(SqlSnippetPlaceholders.SelectedId).Append('$');
                index = end;
                continue;
            }

            if (!placeholders.TryGetValue(id, out var placeholder))
            {
                // 未宣告標記屬於使用者文字，兩端錢字號都必須對原生引擎跳脫。
                AppendLiteral(snippet.Code, index, end - index, text, native);
                index = end;
                continue;
            }

            text.Append(placeholder.DefaultValue);
            native.Append('$').Append(placeholder.Id).Append('$');

            // 同名欄位只宣告一次，順序是它第一次出現的位置。
            if (declared.Add(placeholder.Id))
            {
                fields.Add(placeholder);
            }

            index = end;
        }

        if (caretOffset < 0)
        {
            caretOffset = text.Length;
            native.Append(SqlSnippet.CaretMarker);
        }

        return new SqlSnippetExpansion(text.ToString(), native.ToString(), caretOffset, fields);
    }

    private static string NormalizeLineEndings(string value, string newLine)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (character == '\r')
            {
                if (index + 1 < value.Length && value[index + 1] == '\n')
                {
                    index++;
                }

                builder.Append(newLine);
            }
            else if (character == '\n')
            {
                builder.Append(newLine);
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static void AppendLiteral(char value, StringBuilder text, StringBuilder native)
    {
        text.Append(value);

        if (value == '$')
        {
            native.Append("$$");
        }
        else
        {
            native.Append(value);
        }
    }

    private static void AppendLiteral(
        string value,
        int start,
        int length,
        StringBuilder text,
        StringBuilder native)
    {
        for (var index = start; index < start + length; index++)
        {
            AppendLiteral(value[index], text, native);
        }
    }
}
