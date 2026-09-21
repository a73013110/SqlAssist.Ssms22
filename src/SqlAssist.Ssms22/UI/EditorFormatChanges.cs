using System;
using System.Collections.Generic;

namespace SqlAssist.Ssms22.UI;

/// <summary>共用格式通知篩選；空集合表示全面失效，不為熱路徑建立 params 陣列或 LINQ 委派。</summary>
internal static class EditorFormatChanges
{
    /// <summary>編輯器純文字那一格；底色與前景都問它，名稱是平台定的，不得改寫。</summary>
    public const string PlainText = "Plain Text";

    public static bool Affects(IReadOnlyList<string> changed, string first, string? second = null)
    {
        if (changed.Count == 0) return true;
        for (var i = 0; i < changed.Count; i++)
            if (string.Equals(changed[i], first, StringComparison.OrdinalIgnoreCase) ||
                (second is not null && string.Equals(changed[i], second, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }
}
