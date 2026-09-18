using System;

namespace SqlAssist.Core.Search;

/// <summary>
/// 識別字欄位的共用檢查。
/// </summary>
/// <remarks>
/// 空字串一律當成沒給：這些字串最後會被拿去分組、去重或寫進使用者偏好，
/// 放行一個空的 Id 之後的症狀是兩個不相干的 provider 共用同一個分類 pill，
/// 而且沒有任何一處看得出來是誰給的。
/// </remarks>
internal static class SearchArgument
{
    internal static string Identifier(string? value, string parameterName)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (value.Length == 0) throw new ArgumentException("識別字不可為空字串。", parameterName);
        return value;
    }
}
