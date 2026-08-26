using System;
using SqlAssist.Core.Snippets;

namespace SqlAssist.Core;

/// <summary>
/// 把游標前方剛打完的那個詞元展開成 Snippet，或改寫成大寫的關鍵字。
/// </summary>
/// <remarks>
/// 這是「輸入即展開」的路徑，與建議清單的提交是兩回事。目前 SSMS 端只用了
/// 關鍵字大寫（見 <c>SqlKeywordCasing</c>），Snippet 一律經由清單提交。
/// </remarks>
public sealed class SqlSnippetExpander
{
    private readonly SqlSnippetLibrary _snippets;

    public SqlSnippetExpander() : this(SqlSnippetLibrary.CreateDefault())
    {
    }

    public SqlSnippetExpander(SqlSnippetLibrary snippets)
    {
        _snippets = snippets ?? SqlSnippetLibrary.Empty;
    }

    public bool TryExpand(string textBeforeCaret, out ExpansionResult? result)
    {
        if (string.IsNullOrEmpty(textBeforeCaret))
        {
            result = null;
            return false;
        }

        var tokenStart = FindTokenStart(textBeforeCaret);

        if (tokenStart == textBeforeCaret.Length || !SqlLexicalContext.IsCode(textBeforeCaret, tokenStart))
        {
            result = null;
            return false;
        }

        var token = textBeforeCaret.Substring(tokenStart);

        if (_snippets.TryGet(token, out var snippet))
        {
            result = new ExpansionResult(
                tokenStart,
                token.Length,
                snippet.Expand(out _),
                ExpansionKind.Snippet);
            return true;
        }

        // 關鍵字清單直接向目錄要，不再自己留一份：兩份清單遲早會分岔，
        // 而分岔的症狀是「這個字在清單裡看得到、打完卻不會變大寫」。
        if (SqlKeywordCatalog.TryGetCanonical(token, out var canonical) &&
            !string.Equals(token, canonical, StringComparison.Ordinal))
        {
            result = new ExpansionResult(tokenStart, token.Length, canonical, ExpansionKind.Keyword);
            return true;
        }

        result = null;
        return false;
    }

    private static int FindTokenStart(string text)
    {
        var index = text.Length;

        while (index > 0 && IsTokenCharacter(text[index - 1]))
        {
            index--;
        }

        return index;
    }

    private static bool IsTokenCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }
}
