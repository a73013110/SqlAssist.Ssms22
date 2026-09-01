using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Snippets;

/// <summary>展開後的一個 Tab Stop 欄位。</summary>
/// <remarks>
/// 刻意<b>不</b>在這裡預先算「進入這一格該列什麼」。那份判斷已經有一份，在
/// <c>SqlCompletionContextAnalyzer</c>，而且它要看的是使用者實際編輯過的緩衝區
/// 文字，不是展開當下的預設值——前一格填了什麼會改變後一格的上下文。這裡多存一份
/// 推導結果，改了樣板卻沒改推導時會靜靜地分岔，而症狀只是「清單沒有跳出來」。
///
/// <see cref="Offset"/> 帶出來是因為它是剖析迴圈裡的免費資訊：呼叫端要把
/// 「這一格起點之前的文字」交給分析器時，不必為此再掃一次程式碼。
/// </remarks>
public sealed class SqlSnippetField
{
    internal SqlSnippetField(SqlSnippetPlaceholder placeholder, int offset)
    {
        Placeholder = placeholder;
        Offset = offset;
    }

    public SqlSnippetPlaceholder Placeholder { get; }

    /// <summary>在 <see cref="SqlSnippetExpansion.Text"/> 裡<b>首次</b>出現的位置。</summary>
    /// <remarks>
    /// 同名欄位只記第一次：那既是原生引擎的 Tab 導航順序，也是唯一一個
    /// 「前面的文字還沒被同一格自己影響」的位置。
    /// </remarks>
    public int Offset { get; }
}

/// <summary>Snippet 經過一次剖析後，供一般插入與原生 Expansion 共用的結果。</summary>
public sealed class SqlSnippetExpansion
{
    private SqlSnippetExpansion(
        string text,
        string nativeCode,
        int caretOffset,
        IReadOnlyList<SqlSnippetField> fields)
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
    /// 這就是原生引擎的 Tab 導航順序。只記首次出現的位置，不記每一次：同名欄位的
    /// 同步是引擎自己用標記做的，留一份完整位置表只會變成沒有人讀、卻看起來像
    /// 同步機制的資料。
    /// </remarks>
    public IReadOnlyList<SqlSnippetField> Fields { get; }

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
        var fields = new List<SqlSnippetField>(snippet.Placeholders.Count);
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

            // 起點要在附加預設值之前取，那才是這一格在展開文字裡的開頭。
            var offset = text.Length;
            text.Append(placeholder.DefaultValue);
            native.Append('$').Append(placeholder.Id).Append('$');

            // 同名欄位只宣告一次，順序是它第一次出現的位置。
            if (declared.Add(placeholder.Id))
            {
                fields.Add(new SqlSnippetField(placeholder, offset));
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
