using System;
using System.Collections.Generic;
using System.Threading;

namespace SqlAssist.Core.Snippets;

/// <summary>
/// 包夾清單上一次真的用出去的那一筆。
/// </summary>
/// <remarks>
/// 清單預選哪一筆，本來是「設定檔裡排最前面的那一筆」——而那是分類順序的副作用，
/// 不是任何人決定的。新增一筆可包夾的片段（內建加在前面的分類裡，或使用者自己把
/// 某一格命名成 <c>surround</c>）就會換掉預選項，症狀是按慣包夾再按 Enter 的人
/// 突然包到別的東西，而清單本身看起來完全正常。
///
/// 改成記上一次用過的那一筆之後，預選的理由變成使用者自己的上一個動作，與清單
/// 順序脫鉤；清單仍然<b>維持設定檔的順序</b>，因為使用者記得的是管理介面裡的排法。
///
/// 記的是<b>識別碼</b>不是捷徑：把捷徑改掉之後那仍然是同一筆片段。
///
/// 只活在行程記憶體裡，與 <c>SqlSuggestionUsage</c> 同一套取捨——這份資料的價值
/// 集中在這一段工作期間，為它多一個檔案、一組失效規則與一次磁碟 I/O 並不划算。
/// </remarks>
public static class SqlSnippetSurroundHistory
{
    private static string? _lastKey;

    /// <summary>記下這一次用出去的片段。</summary>
    /// <remarks>
    /// 呼叫點在確認插入成功之後；取消、唯讀或原文已變動都不算用過，
    /// 否則下一次預選會變成根本沒有套用的那一筆。
    /// </remarks>
    public static void Record(SqlSnippet? snippet)
    {
        var key = KeyOf(snippet);

        if (key is not null)
        {
            Volatile.Write(ref _lastKey, key);
        }
    }

    /// <summary>這一份清單要預選第幾筆。</summary>
    /// <remarks>
    /// 沒有紀錄、或記著的那一筆已經不在清單裡（被停用、被刪掉、改成不可包夾）時
    /// 回第一筆：那是清單開起來一定看得到的位置，而不是一個看不見的選取。
    /// </remarks>
    /// <returns>要預選的索引；清單是空的時回 <c>-1</c>。</returns>
    public static int PreferredIndex(IReadOnlyList<SqlSnippet>? candidates)
    {
        if (candidates is null || candidates.Count == 0)
        {
            return -1;
        }

        var key = Volatile.Read(ref _lastKey);

        if (key is not null)
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                if (string.Equals(KeyOf(candidates[index]), key, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
        }

        return 0;
    }

    /// <summary>清空；測試用。</summary>
    public static void Clear() => Volatile.Write(ref _lastKey, null);

    /// <summary>識別碼優先，沒有識別碼時退回捷徑。</summary>
    private static string? KeyOf(SqlSnippet? snippet)
    {
        if (snippet is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(snippet.Id))
        {
            return snippet.Id;
        }

        return string.IsNullOrWhiteSpace(snippet.Shortcut) ? null : snippet.Shortcut;
    }
}
