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

/// <summary>一次繪製的結果：要插入的文字，加上兩個要跟著文字一起搬動的位置。</summary>
public readonly struct SqlSnippetRender
{
    internal SqlSnippetRender(string text, int caretOffset, int surroundOffset, int surroundLength)
    {
        Text = text;
        CaretOffset = caretOffset;
        SurroundOffset = surroundOffset;
        SurroundLength = surroundLength;
    }

    public string Text { get; }

    public int CaretOffset { get; }

    /// <summary>包夾內容在 <see cref="Text"/> 裡的起點；沒有包夾內容時為 -1。</summary>
    public int SurroundOffset { get; }

    public int SurroundLength { get; }

    public bool HasSurround => SurroundOffset >= 0 && SurroundLength > 0;
}

/// <summary>Snippet 經過一次剖析後，供一般插入與原生 Expansion 共用的結果。</summary>
public sealed class SqlSnippetExpansion
{
    private SqlSnippetExpansion(
        string text,
        string nativeCode,
        int caretOffset,
        IReadOnlyList<SqlSnippetField> fields,
        int surroundOffset,
        int surroundLength)
    {
        Text = text;
        NativeCode = nativeCode;
        CaretOffset = caretOffset;
        Fields = fields;
        SurroundOffset = surroundOffset;
        SurroundLength = surroundLength;
    }

    /// <summary>一般插入與原生失敗時使用的完整文字。</summary>
    public string Text { get; }

    /// <summary>
    /// 原生 Snippet XML 的 Code 內容。已知欄位與保留標記維持原樣，
    /// 其餘錢字號已依 VS Snippet 規則加倍。
    /// </summary>
    public string NativeCode { get; }

    public int CaretOffset { get; }

    /// <summary>包夾內容在 <see cref="Text"/> 裡的起點；不是包夾展開時為 -1。</summary>
    /// <remarks>
    /// 給預覽用來把「使用者原本的 SQL」與「片段新增的外框」分開呈現。位置在這裡算是
    /// 免費的（剖析迴圈本來就知道自己填在哪），呼叫端事後再用字串比對找一次，
    /// 遇到外框裡剛好有一段相同文字就會標錯。
    /// </remarks>
    public int SurroundOffset { get; }

    public int SurroundLength { get; }

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
        caretOffset = PrefixLength(CaretOffset, newLine);
        return NormalizeLineEndings(Text, newLine);
    }

    public string GetNativeCode(string newLine) => NormalizeLineEndings(NativeCode, newLine);

    public string GetText(string newLine, string baseIndent, out int caretOffset)
    {
        var render = Render(newLine, baseIndent);
        caretOffset = render.CaretOffset;
        return render.Text;
    }

    /// <summary>一次算出插入用的文字、游標落點與包夾內容的範圍。</summary>
    /// <remarks>
    /// 換行正規化與補縮排都會推移位置，所以三個位置必須跟著文字一起算；分開算的那一份
    /// 會對到另一個字串上。呼叫端只需要文字時仍可用
    /// <see cref="GetText(string, string, out int)"/>，兩者是同一份實作。
    /// </remarks>
    public SqlSnippetRender Render(string newLine, string baseIndent)
    {
        var text = NormalizeLineEndings(Text, newLine);
        var hasSurround = SurroundOffset >= 0;
        var offsets = new[]
        {
            PrefixLength(CaretOffset, newLine),
            hasSurround ? PrefixLength(SurroundOffset, newLine) : -1,
            hasSurround ? PrefixLength(SurroundOffset + SurroundLength, newLine) : -1
        };
        text = SqlSnippetIndentation.Apply(text, baseIndent, offsets);
        return new SqlSnippetRender(
            text,
            offsets[0],
            hasSurround ? offsets[1] : -1,
            hasSurround ? offsets[2] - offsets[1] : 0);
    }

    private int PrefixLength(int offset, string newLine) =>
        NormalizeLineEndings(Text.Substring(0, offset), newLine).Length;

    /// <param name="snippet">要展開的片段。</param>
    /// <param name="surroundText">
    /// 包夾時使用者選取的文字；一般插入時為 <c>null</c>。
    /// </param>
    /// <remarks>
    /// 包夾與插入走的是<b>同一份</b>樣板與同一條展開路徑，差別只有包夾欄位
    /// （<see cref="SqlSnippetPlaceholders.SurroundId"/>）填的是選取的文字而不是預設值，
    /// 而且填了之後就不再是可導航欄位——它已經有內容了，讓引擎把它選起來，
    /// 使用者下一個按鍵就會把自己剛包進去的東西刪掉。
    ///
    /// 其餘欄位照舊是 Tab Stop，所以 <c>wl</c> 包夾之後游標仍然落在條件那一格。
    /// </remarks>
    public static SqlSnippetExpansion Create(SqlSnippet snippet, string? surroundText = null)
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
        var surroundOffset = -1;
        var surroundLength = 0;
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

            if (surroundText is not null &&
                SqlSnippetPlaceholders.IsNamed(id, SqlSnippetPlaceholders.SurroundId))
            {
                surroundOffset = text.Length;
                AppendLiteral(
                    SqlSnippetSurround.Reindent(surroundText, AnchorIndent(text)),
                    text,
                    native);
                surroundLength = text.Length - surroundOffset;
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

        return new SqlSnippetExpansion(
            text.ToString(), native.ToString(), caretOffset, fields, surroundOffset, surroundLength);
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

    /// <summary>目前這一行在錨點之前的縮排；錨點不在行首時沒有縮排可言。</summary>
    /// <remarks>
    /// 從已經產生的文字往回讀，而不是掃樣板：樣板裡的前一格可能已經被預設值換掉，
    /// 長度不同，而要對齊的是<b>展開後</b>的那一行。
    /// </remarks>
    private static string AnchorIndent(StringBuilder text)
    {
        var index = text.Length;

        while (index > 0 && text[index - 1] != '\n')
        {
            if (text[index - 1] != ' ' && text[index - 1] != '\t')
            {
                return string.Empty;
            }

            index--;
        }

        return text.ToString(index, text.Length - index);
    }

    private static void AppendLiteral(string value, StringBuilder text, StringBuilder native) =>
        AppendLiteral(value, 0, value.Length, text, native);

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
