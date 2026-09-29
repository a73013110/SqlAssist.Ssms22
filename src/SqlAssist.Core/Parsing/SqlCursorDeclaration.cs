using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 游標的宣告：<c>DECLARE 名稱 [INSENSITIVE] [SCROLL] CURSOR</c>。
/// </summary>
/// <remarks>
/// 位置分析（<c>CURSOR</c> 之後是選項還是 <c>FOR</c>）與建議清單（<c>OPEN </c>、
/// <c>FETCH NEXT FROM </c> 之後列哪些名稱）問的是同一件事，認法只有這一份；各寫一份的症狀是
/// 一邊認得 <c>DECLARE c SCROLL CURSOR</c>、另一邊不認，選項列得出來、名稱卻列不出來。
///
/// <c>DECLARE @c CURSOR</c> 是游標變數，名稱是變數，由變數那一份收。
/// </remarks>
public static class SqlCursorDeclaration
{
    /// <summary>
    /// <paramref name="cursor"/> 的 <c>CURSOR</c> 宣告了一個具名游標時，回傳名稱的詞元索引；否則 -1。
    /// </summary>
    /// <remarks>
    /// 名稱與 ISO 選項各是一串非關鍵字的識別字，往回走到 <c>DECLARE</c> 為止：
    /// 方括號裡的名稱不是關鍵字（<c>DECLARE [OPEN] CURSOR</c>）。
    /// </remarks>
    public static int FindName(IReadOnlyList<SqlToken> tokens, int cursor)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if (cursor < 0 || cursor >= tokens.Count || !tokens[cursor].IsKeyword("CURSOR"))
        {
            return -1;
        }

        var declare = cursor - 1;

        while (declare >= 0 && IsPlainWord(tokens[declare]))
        {
            declare--;
        }

        return declare >= 0 && declare < cursor - 1 && tokens[declare].IsKeyword("DECLARE")
            ? declare + 1
            : -1;
    }

    /// <summary>這份指令碼宣告的具名游標，依出現順序、不重複。</summary>
    public static IReadOnlyList<string> CollectNames(IReadOnlyList<SqlToken> tokens)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        List<string>? names = null;
        HashSet<string>? seen = null;

        for (var index = 0; index < tokens.Count; index++)
        {
            var name = FindName(tokens, index);

            if (name >= 0 &&
                (seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(tokens[name].Value))
            {
                (names ??= new List<string>()).Add(tokens[name].Value);
            }
        }

        return (IReadOnlyList<string>?)names ?? Array.Empty<string>();
    }

    private static bool IsPlainWord(SqlToken token) =>
        token.Kind == SqlTokenKind.Identifier &&
        (token.IsQuoted || !SqlKeywordCatalog.IsKeyword(token.Value));
}
