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
/// 第一層物件（程序、資料表、序列）是同一條規則的另一份名冊：<see cref="ResolveObject"/> 認同一種名稱格片語，
/// 種類換成清單的目標。
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

    /// <summary>第一層物件的種類與清單目標；<c>TABLE</c> 之後要的是資料來源。</summary>
    private static readonly Dictionary<string, CompletionTarget> ObjectTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PROCEDURE"] = CompletionTarget.Procedure,
        ["PROC"] = CompletionTarget.Procedure,
        ["FUNCTION"] = CompletionTarget.Function,
        ["VIEW"] = CompletionTarget.View,
        ["TRIGGER"] = CompletionTarget.Trigger,
        ["TABLE"] = CompletionTarget.DataSource,
        ["SEQUENCE"] = CompletionTarget.Sequence,
        ["SYNONYM"] = CompletionTarget.Synonym,

        // 中繼資料載入的自訂型別只有資料表型別；別名型別哪裡都不列，這裡不例外。
        ["TYPE"] = CompletionTarget.TableType,
    };

    /// <summary>
    /// 游標那一格是不是第一層物件的既有名稱：名稱格片語的尾巴是種類（<c>ALTER PROCEDURE </c>、<c>DROP TABLE IF EXISTS </c>、
    /// <c>TRUNCATE TABLE </c>、<c>ALTER TABLE t ENABLE TRIGGER </c>），與目錄物件同一條規則。
    /// </summary>
    /// <remarks>
    /// 限定字那一支（<c>ALTER PROCEDURE dbo.</c>）由呼叫端傳限定字之前的詞元與那裡的片語。
    /// <c>CREATE OR ALTER</c> 之後可能是既有的那一個，照 ALTER 算。最長的種類不在名冊裡就不是這一格：
    /// <c>DROP EXTERNAL TABLE </c> 列資料表的話混進一般資料表（第一層快照分不出外部資料表），選到就失敗；
    /// <c>ALTER MATERIALIZED VIEW </c> 只有 Synapse 有。<c>NEXT VALUE FOR</c> 前面沒有種類，不在這裡。
    /// </remarks>
    /// <param name="tokens">名稱那一格之前的詞元。</param>
    /// <param name="phrase">位置分析在那一格比對到的片語。</param>
    /// <returns>那一格要的目標；不是第一層物件的名稱格時為 <c>null</c>。</returns>
    public static SqlObjectNameSlot? ResolveObject(IReadOnlyList<SqlToken> tokens, SqlClausePhraseMatch? phrase)
    {
        if (NamedKind(tokens, phrase, out var start) is not { } kind ||
            !ObjectTargets.TryGetValue(kind, out var target))
        {
            return null;
        }

        var verb = tokens[start >= 1 ? start - 1 : start];
        return new SqlObjectNameSlot(target, verb.Start, verb.IsKeyword("ALTER"));
    }

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

        // 名稱格的尾巴不是種類的（DEFAULT_SCHEMA = 也是名稱格）照下面兩種問。
        if (FromPhrase(tokens, caret.Phrase) is { } named)
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

    private static IReadOnlyList<SqlCatalogEntity>? FromPhrase(IReadOnlyList<SqlToken> tokens, SqlClausePhraseMatch? match)
    {
        if (NameSlotEnd(tokens, match) is not { } end)
        {
            return null;
        }

        if (tokens[end].IsKeyword("AUTHORIZATION") || tokens[end].IsKeyword("MEMBER"))
        {
            return SqlCatalogEntity.Principals(StatementKind(match!.Phrase)?.Scope ?? SqlCatalogScope.Database);
        }

        return NamedKind(tokens, match, out _) is { } kind && SqlCatalogEntity.ForKind(kind) is { } entity
            ? new[] { entity }
            : null;
    }

    /// <summary>
    /// 名稱格片語的尾巴寫的種類（最長的那一個）；<paramref name="start"/> 是種類第一個字。
    /// <c>CREATE 種類</c> 之後是新名字，不算。
    /// </summary>
    private static string? NamedKind(IReadOnlyList<SqlToken> tokens, SqlClausePhraseMatch? match, out int start)
    {
        start = -1;

        return NameSlotEnd(tokens, match) is { } end &&
            LongestKindEndingAt(tokens, end, out start) is { } kind &&
            !(start >= 1 && tokens[start - 1].IsKeyword("CREATE"))
                ? kind
                : null;
    }

    /// <summary>
    /// 確定比對到的名稱格片語（<see cref="SqlClausePhrase.TakesName"/>）的尾巴，<c>IF EXISTS</c> 之前那個詞元；
    /// 不是名稱格時為 <c>null</c>。
    /// </summary>
    /// <remarks>封閉的片語收不了名稱：ALTER AUTHORIZATION ON LOGIN 之後只有 ::，名稱要寫在類別之後。</remarks>
    private static int? NameSlotEnd(IReadOnlyList<SqlToken> tokens, SqlClausePhraseMatch? match)
    {
        if (match is not { IsCertain: true, Phrase: { TakesName: true, IsClosed: false } })
        {
            return null;
        }

        var end = tokens.Count - 1;

        if (end >= 1 && tokens[end].IsKeyword("EXISTS") && tokens[end - 1].IsKeyword("IF"))
        {
            end -= 2;
        }

        return end >= 0 ? end : null;
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
    private static SqlCatalogEntity? KindEndingAt(IReadOnlyList<SqlToken> tokens, int end, out int start) =>
        LongestKindEndingAt(tokens, end, out start) is { } kind ? SqlCatalogEntity.ForKind(kind) : null;

    /// <summary>以 <paramref name="end"/> 結尾的最長建立種類；<paramref name="start"/> 是種類第一個字。</summary>
    private static string? LongestKindEndingAt(IReadOnlyList<SqlToken> tokens, int end, out int start)
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
            return string.Join(" ", words);
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

/// <summary>第一層物件的名稱格：列哪一類、動詞是哪個詞元、是不是改定義。</summary>
/// <param name="Target">那一格的清單目標。</param>
/// <param name="VerbStart">種類前面那個詞元（ALTER、DROP、TRUNCATE、ENABLE）在原文的起點；整句展開從它蓋起。</param>
/// <param name="Alters">動詞是 ALTER（含 <c>CREATE OR ALTER</c>）：提交放進完整定義，放不放得進由物件自己答。</param>
internal sealed record SqlObjectNameSlot(CompletionTarget Target, int VerbStart, bool Alters);
