using System;
using System.Collections.Generic;

namespace SqlAssist.Core.SqlMemory;

/// <summary>
/// 篩選用的一組伺服器或資料庫名稱；空的一組表示不限，與「沒有指定」是同一件事。
/// </summary>
/// <remarks>
/// History、Favorites 與連線名稱面板三份請求都帶這種名單，正規化只寫這一份。各自去重的症狀有兩個：
/// 其一是重複的名稱進了 <c>IN</c>，其二是游標指紋照名單原樣組——同一組條件會因為使用者勾選的先後
/// 算出兩個指紋，而續頁那一刻會被當成換過條件，整份清單跳回第一頁。排序後去重讓指紋與勾選順序無關。
/// 比對一律 ordinal，與儲存層的 <c>=</c> 同語意。
/// </remarks>
public static class SqlConnectionNames
{
    /// <summary>指紋裡的分隔字元；名稱本身不可能含有它，接起來才不會把兩個名字讀成一個。</summary>
    private const string Separator = "";

    /// <summary>去空白、去重、依序排好的名單；null 或全是空白時回空名單。</summary>
    /// <param name="normalize">每一個名稱的額外正規化（收藏標註有長度上限）；回 null 表示這一個不算數。</param>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? names, Func<string, string?>? normalize = null)
    {
        if (names is null) return Array.Empty<string>();
        var unique = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (name is null) continue;
            var value = normalize is null ? name.Trim() : normalize(name);
            if (!string.IsNullOrEmpty(value)) unique.Add(value!);
        }

        if (unique.Count == 0) return Array.Empty<string>();
        var result = new string[unique.Count];
        unique.CopyTo(result);
        return result;
    }

    /// <summary>單一名稱的名單；「只選了一個」仍然走同一條多值的路，儲存層不必認得兩種形狀。</summary>
    public static IReadOnlyList<string> One(string? name) =>
        string.IsNullOrWhiteSpace(name) ? Array.Empty<string>() : new[] { name!.Trim() };

    /// <summary>游標指紋用的一段；空名單是空字串，與「沒有指定」算出同一個指紋。</summary>
    public static string Fingerprint(IReadOnlyList<string> names)
    {
        if (names is null) throw new ArgumentNullException(nameof(names));
        return names.Count == 0 ? "" : string.Join(Separator, names);
    }
}
