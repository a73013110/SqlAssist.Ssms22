using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// <c>COLLATE</c> 之後那兩個不必問伺服器的名稱。
/// </summary>
/// <remarks>
/// 定序名稱本身只有伺服器知道（<c>sys.fn_helpcollations()</c>），這兩個不一樣：
/// 它們是文法上的字，不隨伺服器版本增加，因此寫在這裡。也因此它們是
/// 「查不到定序名單」時那個位置<b>仍然有東西</b>的那一份——連不上、權限不足或
/// 關掉「列出資料庫物件與欄位」都不影響它們，見
/// <see cref="CompletionTarget.Collation"/>。
///
/// <c>CATALOG_DEFAULT</c> 只在全文檢索述詞裡合法，仍然列出來：與資料表提示
/// 同一條取捨——那一份也沒有按敘述種類再篩一次，說得清楚的話寫在說明裡，
/// 而漏掉的名稱使用者完全看不出來。
/// </remarks>
public static class SqlCollationCatalog
{
    private static readonly (string Name, string Description)[] DefaultDefinitions =
    {
        ("DATABASE_DEFAULT", "使用目前資料庫的定序"),
        ("CATALOG_DEFAULT", "使用全文檢索目錄的定序；只在全文檢索述詞裡合法")
    };

    private static readonly object Gate = new();

    private static IReadOnlyList<SqlSuggestion>? _defaults;

    /// <summary><c>DATABASE_DEFAULT</c> 與 <c>CATALOG_DEFAULT</c>。</summary>
    public static IReadOnlyList<SqlSuggestion> Defaults
    {
        get
        {
            lock (Gate)
            {
                return _defaults ??= BuildDefaults();
            }
        }
    }

    /// <summary>查出這兩個名稱的一行說明；大小寫不敏感。</summary>
    public static bool TryGetDescription(string? name, out string description)
    {
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var (candidate, value) in DefaultDefinitions)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                {
                    description = value;
                    return true;
                }
            }
        }

        description = string.Empty;
        return false;
    }

    private static IReadOnlyList<SqlSuggestion> BuildDefaults()
    {
        var suggestions = new List<SqlSuggestion>(DefaultDefinitions.Length);

        foreach (var (name, description) in DefaultDefinitions)
        {
            suggestions.Add(new SqlSuggestion(
                name,
                name,
                description,
                description,
                SuggestionKind.Collation));
        }

        return suggestions;
    }
}
