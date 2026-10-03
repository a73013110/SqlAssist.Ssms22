using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 游標那一格是不是某幾種目錄物件的既有名稱：一條規則——那一格前面寫的是「哪一種」。
/// </summary>
/// <remarks>
/// 種類的字一律取產生器探到的建立種類（CreatedKinds），取最長的那一個：<c>DROP DATABASE SCOPED CREDENTIAL</c>
/// 是資料庫範圍認證，不是伺服器的認證；最長的那一種不在名冊裡（<c>DROP EXTERNAL TABLE</c>）就不是這一格。
/// 「前面寫的是哪一種」有四種寫法，都問同一份名冊：
/// <list type="bullet">
/// <item>類別之後的 <c>::</c>（<c>GRANT … ON SCHEMA::</c>、<c>ALTER AUTHORIZATION ON LOGIN::</c>）。</item>
/// <item>確定比對到的名稱格片語（<see cref="SqlClausePhrase.TakesName"/>），尾巴是種類：<c>ALTER LOGIN </c>、
/// <c>DROP USER IF EXISTS </c>、<c>CREATE USER u FOR LOGIN </c>、<c>OPEN SYMMETRIC KEY </c>。
/// 尾巴是 <c>AUTHORIZATION</c> 或 <c>MEMBER</c> 的是主體，範圍取那一句的種類。
/// <c>CREATE 種類</c> 之後是新名字，不算。</item>
/// <item>清單片語一項的 <c>X =</c>，X 去掉 <c>DEFAULT_</c> 是種類：<c>DEFAULT_DATABASE = </c>、<c>DEFAULT_SCHEMA = </c>、
/// <c>LOGIN = </c>、<c>CREDENTIAL = </c>。語言不在名冊裡，由 <see cref="SqlInstanceList"/> 給。</item>
/// <item>主體的位置（權限的 <c>TO</c>、<c>FROM</c>，含 <c>TO a, </c>）：範圍依 ON 的類別，沒有 ON 時兩層都列。</item>
/// </list>
/// 名冊沒有的種類（資料表、程序）不走這裡，由第一層的物件清單與其餘目標給。
/// </remarks>
internal static class SqlCatalogEntityPosition
{
    /// <summary>建立種類的字，長的在前；<c>OR ALTER</c> 開頭的那幾種是同一種的另一種寫法，不收。</summary>
    private static readonly string[][] Kinds = SqlKeywordCatalogData.CreatedKinds
        .Select(kind => kind.Kind)
        .Where(kind => !kind.StartsWith("OR ", StringComparison.OrdinalIgnoreCase))
        .Select(kind => kind.Split(' '))
        .OrderByDescending(words => words.Length)
        .ToArray();

    /// <param name="tokens">游標<b>之前</b>、不含正在輸入的那個詞元的詞法單元。</param>
    /// <param name="textBeforeToken">同一段原文；清單的錨點與主體的範圍要問位置分析。</param>
    /// <param name="caret">位置分析對游標處的回報；名稱格的片語與主體的位置在裡面。</param>
    /// <returns>那一格的種類；不是目錄物件的名稱格時為 <c>null</c>。</returns>
    public static SqlCatalogEntitySlot? Resolve(
        IReadOnlyList<SqlToken> tokens,
        string textBeforeToken,
        SqlCaretPosition caret)
    {
        if (tokens.Count == 0)
        {
            return null;
        }

        var last = tokens.Count - 1;

        // 類別之後只寫得出名稱：ON 那一格的類別字與關鍵字都不屬於這一格。
        if (tokens[last].IsPunctuation("::"))
        {
            return KindEndingAt(tokens, last - 1, out _) is { } securable
                ? new SqlCatalogEntitySlot(new[] { securable }, SqlKeywordPosition.None, Phrase: null)
                : null;
        }

        // 封閉的片語收不了名稱：ALTER AUTHORIZATION ON LOGIN 之後只有 ::，名稱要寫在類別之後。
        // 名稱格的尾巴不是種類的（DEFAULT_SCHEMA = 也是名稱格）照下面兩種問。
        if (caret.Phrase is { IsCertain: true, Phrase: { TakesName: true, IsClosed: false } phrase } &&
            FromPhrase(tokens, phrase) is { } named)
        {
            return new SqlCatalogEntitySlot(named, caret.Keywords, caret.Phrase);
        }

        // 選項的值那一格判不出位置，整份目錄進場的話名稱被兩百個關鍵字淹掉；那裡寫得出的字（NULL）由片語給。
        if (tokens[last].Kind == SqlTokenKind.Operator && tokens[last].Value == "=")
        {
            return FromOption(tokens, textBeforeToken) is { } option
                ? new SqlCatalogEntitySlot(option, SqlKeywordPosition.None, caret.Phrase)
                : null;
        }

        // 主體那一格的關鍵字（PUBLIC、SCHEMA OWNER）由位置給。
        return caret.Keywords != SqlKeywordPosition.Any &&
            (caret.Keywords & SqlKeywordPosition.PermissionGrantee) != SqlKeywordPosition.None
                ? new SqlCatalogEntitySlot(Grantees(tokens, textBeforeToken), caret.Keywords, caret.Phrase)
                : null;
    }

    private static IReadOnlyList<SqlCatalogEntity>? FromPhrase(IReadOnlyList<SqlToken> tokens, SqlClausePhrase phrase)
    {
        var end = tokens.Count - 1;

        if (end >= 1 && tokens[end].IsKeyword("EXISTS") && tokens[end - 1].IsKeyword("IF"))
        {
            end -= 2;
        }

        if (end < 0)
        {
            return null;
        }

        if (tokens[end].IsKeyword("AUTHORIZATION") || tokens[end].IsKeyword("MEMBER"))
        {
            return SqlCatalogEntity.Principals(StatementKind(phrase)?.Scope ?? SqlCatalogScope.Database);
        }

        return KindEndingAt(tokens, end, out var start) is { } entity && !CreatesNew(tokens, start)
            ? new[] { entity }
            : null;
    }

    /// <summary>
    /// 清單片語一項的 <c>X =</c>：X 去掉 <c>DEFAULT_</c>、底線換成空白之後是名冊的種類。
    /// </summary>
    /// <remarks>
    /// 只認清單片語的一項（位置分析找得到錨點）：<c>UPDATE t SET DEFAULT_SCHEMA = </c> 的 DEFAULT_SCHEMA 是資料行。
    /// 括號清單（端點的 <c>ROLE = </c>、外部資料來源的 <c>CREDENTIAL = </c>）不在裡面：那幾格的值不是這份名冊的名稱。
    /// </remarks>
    private static IReadOnlyList<SqlCatalogEntity>? FromOption(IReadOnlyList<SqlToken> tokens, string textBeforeToken)
    {
        var name = tokens.Count - 2;

        if (name < 1 || tokens[name].Kind != SqlTokenKind.Identifier || tokens[name].IsQuoted)
        {
            return null;
        }

        var option = tokens[name].Value;

        if (option.StartsWith("DEFAULT_", StringComparison.OrdinalIgnoreCase))
        {
            option = option.Substring("DEFAULT_".Length);
        }

        if (SqlCatalogEntity.ForKind(option.Replace('_', ' ')) is not { } entity ||
            SqlKeywordPositionAnalyzer.ForScript(tokens, textBeforeToken).FindPhraseListAnchor(name - 1) < 0)
        {
            return null;
        }

        return new[] { entity };
    }

    /// <summary>
    /// 主體那一格（<see cref="SqlKeywordPosition.PermissionGrantee"/>）：<c>GRANT … TO </c>、<c>REVOKE … FROM </c>、
    /// <c>ALTER AUTHORIZATION ON … TO </c>、稽核動作的 <c>BY </c>。
    /// </summary>
    /// <remarks>
    /// 範圍依 ON 的類別：伺服器那一層的類別（<c>LOGIN::</c>、<c>ENDPOINT::</c>）授給登入與伺服器角色，其餘授給使用者與角色；
    /// 沒有類別的（<c>ON dbo.Loan</c>）是資料庫裡的物件。擁有者（<c>ALTER AUTHORIZATION</c>）取類別住的那一層，
    /// 權限取 <see cref="SqlCatalogEntity.GranteeScope"/>：差在資料庫本身。沒有 ON 的權限（<c>GRANT VIEW SERVER STATE TO</c>、
    /// <c>GRANT CREATE TABLE TO</c>）說不出是哪一層，兩層都列。
    /// </remarks>
    private static IReadOnlyList<SqlCatalogEntity> Grantees(IReadOnlyList<SqlToken> tokens, string textBeforeToken)
    {
        var index = tokens.Count - 1;

        while (index >= 2 && tokens[index].IsPunctuation(",") && tokens[index - 1].Kind == SqlTokenKind.Identifier)
        {
            index -= 2;
        }

        var before = SqlKeywordPositionAnalyzer.ForScript(tokens, textBeforeToken).PositionBefore(index);

        return before != SqlKeywordPosition.Any &&
            (before & SqlKeywordPosition.PermissionTarget) != SqlKeywordPosition.None && OnTarget(tokens, index) is { } scoped
            ? scoped
            : SqlCatalogEntity.PrincipalsOfBothScopes;
    }

    /// <summary><paramref name="to"/> 之前那個 ON 目標的主體範圍。</summary>
    private static IReadOnlyList<SqlCatalogEntity>? OnTarget(IReadOnlyList<SqlToken> tokens, int to)
    {
        var colons = -1;

        for (var index = to - 1; index >= 1; index--)
        {
            var token = tokens[index];

            if (token.IsKeyword("ON"))
            {
                var securable = colons > index + 1 && KindEndingAt(tokens, colons - 1, out var start) is { } found && start == index + 1
                    ? found
                    : null;
                var owner = tokens[index - 1].IsKeyword("AUTHORIZATION");
                var scope = securable is null ? SqlCatalogScope.Database : owner ? securable.Scope : securable.GranteeScope;
                return SqlCatalogEntity.Principals(scope);
            }

            if (token.IsPunctuation("::"))
            {
                colons = index;
            }
            else if (token.Kind != SqlTokenKind.Identifier && !token.IsPunctuation("."))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 以 <paramref name="end"/> 結尾的最長建立種類，在名冊裡的話是那一種；<paramref name="start"/> 是種類第一個字。
    /// </summary>
    private static SqlCatalogEntity? KindEndingAt(IReadOnlyList<SqlToken> tokens, int end, out int start)
    {
        start = -1;

        foreach (var words in Kinds)
        {
            var first = end - words.Length + 1;

            if (first < 0 || !WordsAt(tokens, first, words))
            {
                continue;
            }

            start = first;
            return SqlCatalogEntity.ForKind(string.Join(" ", words));
        }

        return null;
    }

    private static bool WordsAt(IReadOnlyList<SqlToken> tokens, int first, string[] words)
    {
        for (var offset = 0; offset < words.Length; offset++)
        {
            if (!tokens[first + offset].IsKeyword(words[offset]) ||
                (first + offset >= 1 && tokens[first + offset - 1].IsPunctuation(".")))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>種類從 <paramref name="start"/> 寫起，前面是 <c>CREATE</c>（或 <c>CREATE OR ALTER</c>）：那一格是新名字。</summary>
    private static bool CreatesNew(IReadOnlyList<SqlToken> tokens, int start) =>
        (start >= 1 && tokens[start - 1].IsKeyword("CREATE")) ||
        (start >= 3 && tokens[start - 1].IsKeyword("ALTER") && tokens[start - 2].IsKeyword("OR") && tokens[start - 3].IsKeyword("CREATE"));

    /// <summary>
    /// 片語那一句建立或修改的種類（<c>ALTER ROLE {name} ADD MEMBER</c> 的 ROLE）；擁有者與成員的範圍取它住的那一層。
    /// </summary>
    /// <remarks>認的是片語本身的字：擁有者那一格的片語都從動詞寫起。名冊沒有的種類（外部程式庫）住在資料庫裡。</remarks>
    private static SqlCatalogEntity? StatementKind(SqlClausePhrase phrase)
    {
        var words = phrase.Pattern.Split(' ');
        var first = words.Length >= 3 && IsWord(words[0], "CREATE") && IsWord(words[1], "OR") && IsWord(words[2], "ALTER") ? 3
            : words.Length >= 1 && (IsWord(words[0], "CREATE") || IsWord(words[0], "ALTER")) ? 1
            : -1;

        if (first < 0)
        {
            return null;
        }

        foreach (var kind in Kinds)
        {
            if (first + kind.Length <= words.Length &&
                kind.Select((word, offset) => IsWord(words[first + offset], word)).All(matches => matches))
            {
                return SqlCatalogEntity.ForKind(string.Join(" ", kind));
            }
        }

        return null;
    }

    private static bool IsWord(string item, string word) => string.Equals(item, word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>目錄物件那一格：要哪幾種、關鍵字照哪一個位置挑、片語的字帶不帶。</summary>
/// <param name="Entities">那一格的種類。</param>
/// <param name="Keywords">
/// 關鍵字照這個位置過濾；那一格寫不出關鍵字時是 <see cref="SqlKeywordPosition.None"/>（<c>::</c> 與選項的值之後），
/// 判不出位置的那份整份目錄不該淹掉名稱。
/// </param>
/// <param name="Phrase">那一格的片語；類別之後不帶：ON 那一格的類別字不屬於名稱。</param>
internal sealed record SqlCatalogEntitySlot(
    IReadOnlyList<SqlCatalogEntity> Entities,
    SqlKeywordPosition Keywords,
    SqlClausePhraseMatch? Phrase);
