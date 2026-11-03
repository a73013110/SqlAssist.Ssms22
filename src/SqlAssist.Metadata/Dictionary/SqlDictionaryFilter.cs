using System;
using System.Collections.Generic;
using SqlAssist.Core.Matching;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>過濾字比對哪些名稱。</summary>
[Flags]
public enum SqlDictionaryMatchTargets
{
    None = 0,

    /// <summary>資料表與檢視的名稱（含結構描述）。</summary>
    Objects = 1,

    /// <summary>資料行名稱。</summary>
    Columns = 2,

    Both = Objects | Columns
}

/// <summary>
/// 資料字典的過濾：主從與資料字典兩種檢視共用<b>一條</b>規則。
/// </summary>
/// <remarks>
/// 規則只有一條，對象是「一個資料行」：
/// 物件名稱命中（有勾資料表）<b>或</b>資料行名稱命中（有勾資料行），再疊上「只看沒說明」與「只看不一致」。
/// 主清單列出至少有一個資料行命中的物件，明細列出命中的那幾個——所以打物件名稱時明細是整張表，
/// 打資料行名稱時明細只剩那幾個，兩種檢視看到的是同一組資料行。各寫一套的症狀是同一個過濾字
/// 在兩種檢視裡數出不同的筆數。
///
/// 預設不含檢視：檢視的資料行多半是資料表那幾個的投影，說明也該寫在資料表上；列進來只是讓同名比較
/// 多出一堆「不一致」，而要改的那一句不在它們身上。要看再打開。
///
/// 字面比對而不是模糊比對：這個過濾是拿來找「同名或相近名稱的資料行」比型別與說明，
/// 模糊比對讓 <c>no</c> 命中每一個散落著 n 與 o 的名稱，比較就失去意義。
/// 以空白或逗號分開的幾個字任一命中就算（<c>ReaderNo, ReaderId</c> 一起比）。
/// </remarks>
public sealed class SqlDictionaryFilter
{
    private static readonly char[] Separators = { ' ', '\t', ',', ';' };

    private readonly IReadOnlyList<TextMatcher> _matchers;

    public SqlDictionaryFilter(
        string? text = null,
        TextMatchOptions options = TextMatchOptions.None,
        SqlDictionaryMatchTargets targets = SqlDictionaryMatchTargets.Both,
        bool onlyMissingDescriptions = false,
        bool onlyInconsistent = false,
        bool includeViews = false)
    {
        Text = text?.Trim() ?? string.Empty;
        Options = options;
        Targets = targets == SqlDictionaryMatchTargets.None ? SqlDictionaryMatchTargets.Both : targets;
        OnlyMissingDescriptions = onlyMissingDescriptions;
        OnlyInconsistent = onlyInconsistent;
        IncludeViews = includeViews;

        var matchers = new List<TextMatcher>();
        foreach (var term in Text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            matchers.Add(new TextMatcher(term, options));
        }

        _matchers = matchers;
    }

    /// <summary>剛打開時的過濾：沒有過濾字、不限資料行、不含檢視。</summary>
    public static SqlDictionaryFilter Default { get; } = new();

    public string Text { get; }

    public TextMatchOptions Options { get; }

    /// <summary>過濾字比對哪些名稱；兩個都沒勾等於兩個都勾，否則任何字都篩成一列都沒有。</summary>
    public SqlDictionaryMatchTargets Targets { get; }

    public bool OnlyMissingDescriptions { get; }

    public bool OnlyInconsistent { get; }

    public bool IncludeViews { get; }

    /// <summary>資料行這一層有沒有條件；沒有時留下的物件整張列出，不必逐一比對。</summary>
    private bool HasColumnConditions => _matchers.Count > 0 || OnlyMissingDescriptions || OnlyInconsistent;

    /// <summary>過濾字有沒有命中這個物件的名稱；有勾資料表時才算。</summary>
    public bool MatchesObjectName(SqlDictionaryObject item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        return (Targets & SqlDictionaryMatchTargets.Objects) != 0 && MatchesAny(item.DisplayName);
    }

    /// <summary>過濾字有沒有命中這個資料行的名稱；有勾資料行時才算。</summary>
    public bool MatchesColumnName(SqlDictionaryColumn column)
    {
        if (column is null) throw new ArgumentNullException(nameof(column));
        return (Targets & SqlDictionaryMatchTargets.Columns) != 0 && MatchesAny(column.Name);
    }

    /// <summary>這個物件留不留（不看資料行）：只有種類這一條。</summary>
    public bool Includes(SqlDictionaryObject item) =>
        IncludeViews || (item ?? throw new ArgumentNullException(nameof(item))).Kind != SqlDictionaryObjectKind.View;

    /// <summary>
    /// 這個資料行留不留。
    /// </summary>
    /// <param name="objectNameMatched">
    /// 這個物件的名稱是否命中，由呼叫端算一次傳進來：一張表幾十個資料行不必每一個都重比一次表名。
    /// </param>
    /// <param name="currentDescription">現在的說明（含還沒寫回的編輯）。</param>
    /// <param name="group">同名比較；「只看不一致」要它。</param>
    public bool Matches(
        SqlDictionaryColumn column,
        bool objectNameMatched,
        string currentDescription,
        SqlColumnNameGroup? group)
    {
        if (column is null) throw new ArgumentNullException(nameof(column));

        if (_matchers.Count > 0 && !objectNameMatched && !MatchesColumnName(column)) return false;
        if (OnlyMissingDescriptions && SqlDescriptionValue.Normalize(currentDescription).Length > 0) return false;
        if (OnlyInconsistent && group is not { IsInconsistent: true }) return false;
        return true;
    }

    /// <summary>
    /// 一個物件命中的資料行，照原本的順序；物件本身被濾掉或一個都沒命中時是空清單。
    /// </summary>
    public IReadOnlyList<SqlDictionaryColumn> MatchingColumns(
        SqlDictionaryObject item,
        Func<SqlDescriptionTarget, string, string> currentDescription,
        SqlColumnNameGroups groups)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (currentDescription is null) throw new ArgumentNullException(nameof(currentDescription));
        if (groups is null) throw new ArgumentNullException(nameof(groups));

        if (!Includes(item)) return Array.Empty<SqlDictionaryColumn>();
        if (!HasColumnConditions) return item.Columns;

        var objectNameMatched = _matchers.Count > 0 && MatchesObjectName(item);
        List<SqlDictionaryColumn>? matched = null;

        foreach (var column in item.Columns)
        {
            var description = currentDescription(item.TargetOf(column), column.Description);
            if (!Matches(column, objectNameMatched, description, groups.Find(column.Name))) continue;
            (matched ??= new List<SqlDictionaryColumn>()).Add(column);
        }

        return matched ?? (IReadOnlyList<SqlDictionaryColumn>)Array.Empty<SqlDictionaryColumn>();
    }

    private bool MatchesAny(string name)
    {
        foreach (var matcher in _matchers)
        {
            if (matcher.IsMatch(name)) return true;
        }

        return false;
    }
}
