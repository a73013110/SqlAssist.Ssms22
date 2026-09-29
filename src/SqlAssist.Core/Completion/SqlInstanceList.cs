using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 只有伺服器執行個體知道的一份名單：定序、語言、時區。
/// </summary>
/// <remarks>
/// 三份是同一種東西：剖析器在那一格什麼名稱都收，名單只在伺服器上
/// （<c>sys.fn_helpcollations()</c>、<c>sys.syslanguages</c>、<c>sys.time_zone_info</c>），
/// 而使用者要的幾乎一定是「現在在用的那一個」或「這份指令碼上面寫過的那一個」。
/// 所以位置、指令碼已用值、排名分級與插入文字都只寫一次，每份名單只交代
/// 自己不一樣的地方：前面接什麼字、值寫成什麼樣子、有哪些不必問伺服器的字。
///
/// 位置與「指令碼已用值」是同一條規則：值就是寫在那個位置上的詞元，
/// 掃指令碼時對每一個詞元問「它前面是不是這份名單的位置」，不另寫一份掃描。
/// </remarks>
public sealed class SqlInstanceList
{
    public static readonly SqlInstanceList Collation = new(
        CompletionTarget.Collation,
        SqlInstanceValueForm.Bare,
        // 三種 COLLATE（運算式之後、資料行定義、CREATE／ALTER DATABASE）前面長得都不一樣，
        // 後面要的東西卻完全一樣，所以只認 COLLATE 這個字。
        new[] { new Lead(startsStatement: false, "COLLATE") },
        () => SqlKindText.Collation,
        () => InstanceListText.CollationInUse,
        () => InstanceListText.CollationInScript,
        // CATALOG_DEFAULT 只在全文檢索述詞裡合法，仍然列出來：與資料表提示同一條取捨，
        // 說得清楚的話寫在說明裡，而漏掉的名稱使用者完全看不出來。
        ("DATABASE_DEFAULT", () => InstanceListText.CollationDatabaseDefault),
        ("CATALOG_DEFAULT", () => InstanceListText.CollationCatalogDefault));

    public static readonly SqlInstanceList Language = new(
        CompletionTarget.Language,
        SqlInstanceValueForm.Identifier,
        new[]
        {
            // SET 要是一句的開頭：UPDATE t SET Language 的 Language 是資料行。
            new Lead(startsStatement: true, "SET", "LANGUAGE"),
            // CREATE／ALTER LOGIN、CREATE／ALTER USER、CREATE／ALTER DATABASE 的選項都是這一種。
            new Lead(startsStatement: false, "DEFAULT_LANGUAGE", "=")
        },
        () => SqlKindText.Language,
        () => InstanceListText.LanguageInUse,
        () => InstanceListText.LanguageInScript);

    public static readonly SqlInstanceList TimeZone = new(
        CompletionTarget.TimeZone,
        SqlInstanceValueForm.String,
        new[] { new Lead(startsStatement: false, "AT", "TIME", "ZONE") },
        () => SqlKindText.TimeZone,
        () => InstanceListText.TimeZoneInUse,
        () => InstanceListText.TimeZoneInScript);

    /// <summary>全部的名單；中繼資料層以此核對每一份都有查詢。</summary>
    public static IReadOnlyList<SqlInstanceList> All { get; } = new[] { Collation, Language, TimeZone };

    private readonly Lead[] _leads;
    private readonly Func<string> _kindText;
    private readonly Func<string> _inUseText;
    private readonly Func<string> _scriptText;
    private readonly (string Name, Func<string> Description)[] _defaultDefinitions;
    private readonly SqlLanguageCache<IReadOnlyList<SqlSuggestion>> _defaults;

    private SqlInstanceList(
        CompletionTarget target,
        SqlInstanceValueForm form,
        Lead[] leads,
        Func<string> kindText,
        Func<string> inUseText,
        Func<string> scriptText,
        params (string Name, Func<string> Description)[] defaults)
    {
        Target = target;
        Form = form;
        _leads = leads;
        _kindText = kindText;
        _inUseText = inUseText;
        _scriptText = scriptText;
        _defaultDefinitions = defaults;
        _defaults = new SqlLanguageCache<IReadOnlyList<SqlSuggestion>>(_ => BuildDefaults());
    }

    /// <summary>這份名單的位置；每份名單一個目標。</summary>
    public CompletionTarget Target { get; }

    /// <summary>值寫進指令碼的樣子。</summary>
    public SqlInstanceValueForm Form { get; }

    /// <summary>種類名稱；通知主體與沒有細節的列尾說明。</summary>
    public string KindText => _kindText();

    /// <summary>
    /// 文法上的字，不必問伺服器（<c>DATABASE_DEFAULT</c>、<c>CATALOG_DEFAULT</c>）。
    /// </summary>
    /// <remarks>
    /// 它們不隨伺服器版本增加，因此寫在這裡；也因此是查不到名單時那個位置
    /// <b>仍然有東西</b>的那一份——連不上、權限不足或關掉「列出資料庫物件與欄位」都不影響。
    /// </remarks>
    public IReadOnlyList<SqlSuggestion> Defaults => _defaults.Current;

    /// <summary>目標對應的名單；不是名單位置時為 <c>null</c>。</summary>
    public static SqlInstanceList? For(CompletionTarget target)
    {
        foreach (var list in All)
        {
            if (list.Target == target)
            {
                return list;
            }
        }

        return null;
    }

    /// <summary>
    /// 判斷 <paramref name="tokens"/> 的尾端之後是不是某一份名單的值。
    /// </summary>
    /// <param name="text"><paramref name="tokens"/> 的原文；判斷語句開頭要看換行。</param>
    /// <param name="tokens">游標<b>之前</b>、不含正在輸入的那個詞元的詞法單元。</param>
    /// <param name="list">判定成立時的名單。</param>
    public static bool TryResolve(string text, IReadOnlyList<SqlToken> tokens, out SqlInstanceList list)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        SqlStatementBoundaries? boundaries = null;

        foreach (var candidate in All)
        {
            if (candidate.IntroducesValueAt(text, tokens, tokens.Count, ref boundaries))
            {
                list = candidate;
                return true;
            }
        }

        list = null!;
        return false;
    }

    /// <summary>
    /// 這份指令碼在這份名單的位置上已經寫過的值。
    /// </summary>
    /// <remarks>
    /// 使用者要在第二個 <c>COLLATE</c> 之後打的，幾乎一定是他第一個已經寫過的那一個——
    /// 兩邊定序不一樣正是那句 <c>COLLATE</c> 要修的問題；<c>AT TIME ZONE</c> 一來一回
    /// 也常是同一組時區。這一份不必問伺服器，因此連不上或關掉資料庫物件時都還在。
    ///
    /// 只在游標真的落在名單位置時才掃：掃描是單趟線性的，但那條路徑在每一次按鍵上。
    /// </remarks>
    /// <param name="text">整份指令碼。</param>
    /// <param name="tokens"><paramref name="text"/> 的詞法單元。</param>
    /// <param name="caretPosition">
    /// 游標位置；碰到游標的那個詞元是正在打的前綴，不是寫過的值——收進來的話 <c>COLLATE Chin</c>
    /// 的 <c>Chin</c> 會排在真正的定序前面。
    /// </param>
    public IReadOnlyList<SqlSuggestion> ScriptValues(string text, IReadOnlyList<SqlToken> tokens, int caretPosition)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        List<SqlSuggestion>? suggestions = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SqlStatementBoundaries? boundaries = null;

        for (var index = 1; index < tokens.Count; index++)
        {
            // 文法上的字已經在 Defaults 裡，再放一份會讓同一個名稱在清單上出現兩次。
            if ((tokens[index].Start <= caretPosition && caretPosition <= tokens[index].End) ||
                !TryReadValue(tokens[index], out var name) ||
                IsDefault(name) ||
                seen.Contains(name) ||
                !IntroducesValueAt(text, tokens, index, ref boundaries))
            {
                continue;
            }

            seen.Add(name);
            (suggestions ??= new List<SqlSuggestion>())
                .Add(Create(name, _scriptText(), SuggestionKind.InstanceListValueInUse));
        }

        return (IReadOnlyList<SqlSuggestion>?)suggestions ?? Array.Empty<SqlSuggestion>();
    }

    /// <summary>
    /// 這個位置的完整清單：文法上的字、指令碼已用值、伺服器在用的那一個、其餘名單。
    /// </summary>
    /// <remarks>
    /// 分兩級而不是另寫一套排名，理由與 <see cref="SuggestionKind.ScriptDataSource"/> 對
    /// <see cref="SuggestionKind.Table"/> 相同：東西是同一種，排名必須不同。定序五千多個名稱
    /// 只差 <c>_CI_AS</c> 這種尾巴，模糊比對撈回來的順序沒有意義。同一個名稱只列一次，
    /// 先列的那一級留下。
    /// </remarks>
    /// <param name="scriptValues">由 <see cref="ScriptValues"/> 收集、放在上下文裡的那一份。</param>
    /// <param name="server">伺服器的回答；沒問或問不到時是 <see cref="SqlInstanceListData.Empty"/>。</param>
    public IReadOnlyList<SqlSuggestion> Suggestions(
        IReadOnlyList<SqlSuggestion> scriptValues,
        SqlInstanceListData server)
    {
        if (scriptValues is null)
        {
            throw new ArgumentNullException(nameof(scriptValues));
        }

        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }

        var defaults = Defaults;
        var suggestions = new List<SqlSuggestion>(defaults.Count + scriptValues.Count + server.Entries.Count + 1);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var suggestion in defaults)
        {
            seen.Add(suggestion.DisplayText);
            suggestions.Add(suggestion);
        }

        foreach (var suggestion in scriptValues)
        {
            if (seen.Add(suggestion.DisplayText))
            {
                suggestions.Add(suggestion);
            }
        }

        // 名單查不到而在用的那一個查得到時仍然列出它：那一個正是使用者最常要的。
        if (server.InUse is { } inUse && seen.Add(inUse))
        {
            suggestions.Add(Create(inUse, _inUseText(), SuggestionKind.InstanceListValueInUse));
        }

        foreach (var entry in server.Entries)
        {
            if (seen.Add(entry.Name))
            {
                suggestions.Add(Create(entry.Name, entry.Detail ?? _kindText(), SuggestionKind.InstanceListValue));
            }
        }

        return suggestions;
    }

    /// <summary>
    /// 第 <paramref name="end"/> 個詞元的位置是這份名單的值：它前面緊接著一組前導字。
    /// </summary>
    private bool IntroducesValueAt(
        string text,
        IReadOnlyList<SqlToken> tokens,
        int end,
        ref SqlStatementBoundaries? boundaries)
    {
        foreach (var lead in _leads)
        {
            var start = end - lead.Words.Length;

            if (start < 0 || !lead.Matches(tokens, start))
            {
                continue;
            }

            if (!lead.StartsStatement ||
                (boundaries ??= new SqlStatementBoundaries(text, tokens)).IsStatementHead(start))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 讀出寫在名單位置上的值；形狀不像這份名單的值（變數、數字、資料行參考）時為 <c>false</c>。
    /// </summary>
    /// <remarks>
    /// 沒加括號的關鍵字不是值：前導字後面還空著時，緊接著的是下一個子句
    /// （<c>SELECT a COLLATE | FROM t</c> 的 <c>FROM</c>、換行後的 <c>GO</c>）。
    /// 定序與語言名稱都不是關鍵字，時區只收字串，這一條不會擋掉真的值。
    /// </remarks>
    private bool TryReadValue(SqlToken token, out string name)
    {
        name = string.Empty;

        if (token.Kind == SqlTokenKind.Identifier && !token.IsQuoted && IsKeywordLike(token.Value))
        {
            return false;
        }

        switch (Form)
        {
            // 加引號的識別字不是定序名稱：COLLATE [x] 是語法錯誤。
            case SqlInstanceValueForm.Bare when token.Kind == SqlTokenKind.Identifier && !token.IsQuoted:
            case SqlInstanceValueForm.Identifier when token.Kind == SqlTokenKind.Identifier:
                name = token.Value;
                return name.Length > 0;

            // SET LANGUAGE N'Deutsch' 與 AT TIME ZONE 'UTC'；AT TIME ZONE 後面的識別字是資料行。
            case SqlInstanceValueForm.Identifier when token.Kind == SqlTokenKind.String:
            case SqlInstanceValueForm.String when token.Kind == SqlTokenKind.String:
                return SqlStringLiteral.TryUnquote(token.Text, out name) && name.Length > 0;

            default:
                return false;
        }
    }

    private static bool IsKeywordLike(string word) =>
        SqlKeywordCatalog.IsKeyword(word) || SqlKeywordCatalog.IsReservedIdentifier(word);

    private bool IsDefault(string name)
    {
        foreach (var (candidate, _) in _defaultDefinitions)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private SqlSuggestion Create(string name, string description, SuggestionKind kind)
    {
        // Tag 帶著名單本身：三份共用兩個 SuggestionKind，圖示要分得出是哪一份。
        return new SqlSuggestion(
            name,
            SqlInsertionText.InstanceListValue(name, Form),
            description,
            description,
            kind,
            tag: this);
    }

    private IReadOnlyList<SqlSuggestion> BuildDefaults()
    {
        var suggestions = new List<SqlSuggestion>(_defaultDefinitions.Length);

        foreach (var (name, describe) in _defaultDefinitions)
        {
            suggestions.Add(Create(name, describe(), SuggestionKind.InstanceListValue));
        }

        return suggestions;
    }

    /// <summary>值前面緊接著的那一組字。</summary>
    private sealed class Lead
    {
        public Lead(bool startsStatement, params string[] words)
        {
            StartsStatement = startsStatement;
            Words = words;
        }

        /// <summary>第一個字必須是一句的開頭。</summary>
        public bool StartsStatement { get; }

        public string[] Words { get; }

        public bool Matches(IReadOnlyList<SqlToken> tokens, int start)
        {
            for (var offset = 0; offset < Words.Length; offset++)
            {
                var token = tokens[start + offset];
                var word = Words[offset];

                var matches = word == "="
                    ? token.Kind == SqlTokenKind.Operator && token.Text == "="
                    : token.IsKeyword(word);

                if (!matches)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
