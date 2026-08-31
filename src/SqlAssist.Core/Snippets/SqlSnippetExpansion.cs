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
        IReadOnlyList<SqlSnippetExpansionField> fields)
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

    /// <summary>依程式碼首次出現順序排列的欄位。</summary>
    public IReadOnlyList<SqlSnippetExpansionField> Fields { get; }

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

        var fieldBuilders = new Dictionary<string, FieldBuilder>(StringComparer.OrdinalIgnoreCase);
        var orderedFields = new List<FieldBuilder>(snippet.Placeholders.Count);
        var text = new StringBuilder(snippet.Code.Length);
        var native = new StringBuilder(snippet.Code.Length);
        var caretOffset = -1;
        var index = 0;

        while (index < snippet.Code.Length)
        {
            if (snippet.Code[index] != '$' || !TryReadMarker(snippet.Code, index, out var id, out var end))
            {
                AppendLiteral(snippet.Code[index], text, native);
                index++;
                continue;
            }

            if (string.Equals(id, "end", StringComparison.OrdinalIgnoreCase))
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

            if (string.Equals(id, "selected", StringComparison.OrdinalIgnoreCase))
            {
                // Completion 提交沒有選取文字；原生引擎仍保留官方的 $selected$ 語意。
                native.Append("$selected$");
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

            var start = text.Length;
            text.Append(placeholder.DefaultValue);
            native.Append('$').Append(placeholder.Id).Append('$');

            if (!fieldBuilders.TryGetValue(placeholder.Id, out var builder))
            {
                builder = new FieldBuilder(placeholder);
                fieldBuilders.Add(placeholder.Id, builder);
                orderedFields.Add(builder);
            }

            builder.Occurrences.Add(new SqlSnippetFieldOccurrence(start, placeholder.DefaultValue.Length));
            index = end;
        }

        if (caretOffset < 0)
        {
            caretOffset = text.Length;
            native.Append(SqlSnippet.CaretMarker);
        }

        var fields = new SqlSnippetExpansionField[orderedFields.Count];

        for (var fieldIndex = 0; fieldIndex < orderedFields.Count; fieldIndex++)
        {
            fields[fieldIndex] = orderedFields[fieldIndex].Build();
        }

        return new SqlSnippetExpansion(text.ToString(), native.ToString(), caretOffset, fields);
    }

    private static bool TryReadMarker(string code, int open, out string id, out int end)
    {
        id = string.Empty;
        end = open + 1;

        if (end >= code.Length || !SqlSnippetPlaceholders.IsNameStart(code[end]))
        {
            return false;
        }

        end++;

        while (end < code.Length && SqlSnippetPlaceholders.IsNamePart(code[end]))
        {
            end++;
        }

        if (end >= code.Length || code[end] != '$')
        {
            return false;
        }

        id = code.Substring(open + 1, end - open - 1);
        end++;
        return true;
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

    private sealed class FieldBuilder
    {
        public FieldBuilder(SqlSnippetPlaceholder placeholder)
        {
            Placeholder = placeholder;
        }

        public SqlSnippetPlaceholder Placeholder { get; }

        public List<SqlSnippetFieldOccurrence> Occurrences { get; } = new();

        public SqlSnippetExpansionField Build() => new(
            Placeholder.Id,
            Placeholder.DefaultValue,
            Placeholder.ToolTip,
            Occurrences.ToArray());
    }
}

public sealed class SqlSnippetExpansionField
{
    public SqlSnippetExpansionField(
        string id,
        string defaultValue,
        string toolTip,
        IReadOnlyList<SqlSnippetFieldOccurrence> occurrences)
    {
        Id = id;
        DefaultValue = defaultValue;
        ToolTip = toolTip;
        Occurrences = occurrences;
    }

    public string Id { get; }

    public string DefaultValue { get; }

    public string ToolTip { get; }

    public IReadOnlyList<SqlSnippetFieldOccurrence> Occurrences { get; }
}

public readonly struct SqlSnippetFieldOccurrence
{
    public SqlSnippetFieldOccurrence(int start, int length)
    {
        Start = start;
        Length = length;
    }

    public int Start { get; }

    public int Length { get; }
}
