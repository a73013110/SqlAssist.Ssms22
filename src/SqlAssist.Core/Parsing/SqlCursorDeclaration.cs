using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 游標的宣告：<c>DECLARE 名稱 [INSENSITIVE] [SCROLL] CURSOR</c>、<c>DECLARE @c CURSOR</c>、<c>SET @c = CURSOR</c>。
/// </summary>
/// <remarks>
/// 位置分析（<c>CURSOR</c> 之後是選項還是 <c>FOR</c>）與建議清單（<c>OPEN </c>、
/// <c>FETCH NEXT FROM </c> 之後列哪些名稱）問的是同一件事，認法只有這一份；各寫一份的症狀是
/// 一邊認得 <c>DECLARE c SCROLL CURSOR</c>、另一邊不認，選項列得出來、名稱卻列不出來。
///
/// 游標變數也是游標：<c>OPEN</c>、<c>FETCH</c>、<c>DEALLOCATE</c> 接得了它，所以收進同一份名單。
/// 它的內容在 <c>SET @c = CURSOR</c> 才給，選項也寫在那裡；宣告那一句後面只接逗號或下一句。
/// </remarks>
public static class SqlCursorDeclaration
{
    /// <summary>
    /// <paramref name="cursor"/> 的 <c>CURSOR</c> 宣告或指派了一個游標時，回傳名稱的詞元索引；否則 -1。
    /// </summary>
    /// <remarks>
    /// 名稱與 ISO 選項各是一串非關鍵字的識別字，往回走到 <c>DECLARE</c> 為止：
    /// 方括號裡的名稱不是關鍵字（<c>DECLARE [OPEN] CURSOR</c>）。變數是宣告清單的一項（<c>DECLARE @n int, @c CURSOR</c>，
    /// 可以先寫 <c>AS</c>），或是 <c>SET @c =</c> 的左邊。
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

        if (cursor >= 3 && tokens[cursor - 1] is { Kind: SqlTokenKind.Operator, Value: "=" } && tokens[cursor - 2].Kind == SqlTokenKind.Variable &&
            tokens[cursor - 3].IsKeyword("SET"))
        {
            return cursor - 2;
        }

        if (DeclaredVariable(tokens, cursor) is var variable and >= 0)
        {
            return variable;
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

    /// <summary>
    /// <paramref name="cursor"/> 的 <c>CURSOR</c> 之後接游標選項與 <c>FOR</c>：游標的內容在這裡給。
    /// </summary>
    /// <remarks>變數的宣告只說它是游標，選項寫在之後的 <c>SET @c = CURSOR</c>。</remarks>
    public static bool TakesOptions(IReadOnlyList<SqlToken> tokens, int cursor) =>
        FindName(tokens, cursor) >= 0 && DeclaredVariable(tokens, cursor) < 0;

    /// <summary>
    /// <paramref name="position"/> 前一個詞元是 <c>SET @c =</c> 的等號，而 <c>@c</c> 是這份指令碼的游標變數：
    /// 右邊是一個游標（<c>SET @c = c1</c>）或新的定義（<c>SET @c = CURSOR FOR …</c>）。
    /// </summary>
    public static bool AssignsCursorVariable(IReadOnlyList<SqlToken> tokens, int position)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        var equals = tokens.Count - 1;

        while (equals >= 0 && tokens[equals].End > position)
        {
            equals--;
        }

        if (equals < 2 ||
            tokens[equals] is not { Kind: SqlTokenKind.Operator, Value: "=" } ||
            tokens[equals - 1].Kind != SqlTokenKind.Variable ||
            !tokens[equals - 2].IsKeyword("SET"))
        {
            return false;
        }

        var variable = tokens[equals - 1].Value;

        foreach (var name in CollectNames(tokens))
        {
            if (string.Equals(name, variable, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="last"/> 之後是游標查詢 <c>FOR UPDATE OF</c> 的一個資料行：<c>OF</c> 本身，或清單一項之後的逗號。
    /// </summary>
    /// <remarks>
    /// 清單裡是游標查詢那些來源的既有資料行，可以寫限定字（<c>OF c.CopyNo</c>）。不認的話 OF 之後判不出位置，
    /// 每一個附加片語的字都進場；逗號往回借到查詢的 FROM，清單換成資料表。<c>FOR</c> 後面的 UPDATE 不是動詞，
    /// 範圍分析也不把 OF 讀成它的目標（<see cref="SqlKeywordPositionAnalyzer"/> 的 DML 目標要動詞是一句的開頭）。
    /// 位置分析與上下文分析的欄位來源都問這一條。
    /// </remarks>
    public static bool ListsUpdateColumns(IReadOnlyList<SqlToken> tokens, int last)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        var index = last;

        while (index >= 1 && index < tokens.Count && tokens[index].IsPunctuation(","))
        {
            if (!SqlTokenNavigator.IsNamePart(tokens[index - 1]))
            {
                return false;
            }

            index = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, index - 1) - 1;
        }

        return index >= 2 && index < tokens.Count &&
            tokens[index].IsKeyword("OF") && tokens[index - 1].IsKeyword("UPDATE") && tokens[index - 2].IsKeyword("FOR");
    }

    /// <summary>這份指令碼宣告的游標與游標變數，依出現順序、不重複。</summary>
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

    /// <summary>
    /// <paramref name="cursor"/> 的 <c>CURSOR</c> 是宣告清單一項的型別（<c>DECLARE @n int, @c CURSOR</c>、<c>DECLARE @c AS CURSOR</c>）時，
    /// 回傳變數的詞元索引；否則 -1。
    /// </summary>
    private static int DeclaredVariable(IReadOnlyList<SqlToken> tokens, int cursor)
    {
        var variable = cursor >= 1 && tokens[cursor - 1].IsKeyword("AS") ? cursor - 2 : cursor - 1;

        return variable >= 1 && tokens[variable].Kind == SqlTokenKind.Variable &&
            (tokens[variable - 1].IsKeyword("DECLARE") || tokens[variable - 1].IsPunctuation(","))
                ? variable
                : -1;
    }

    private static bool IsPlainWord(SqlToken token) =>
        token.Kind == SqlTokenKind.Identifier &&
        (token.IsQuoted || !SqlKeywordCatalog.IsKeyword(token.Value));
}
