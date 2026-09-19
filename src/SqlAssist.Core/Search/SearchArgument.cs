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

    /// <summary>
    /// 給人看的一句話：可以很長，但不可以沒有。
    /// </summary>
    /// <remarks>
    /// 說不出原因的「讀不到」與泛用的「部分結果」在畫面上一模一樣，而那正是
    /// <see cref="ISearchSink.ReportUnavailable(string)"/> 存在的理由。
    /// </remarks>
    internal static string Reason(string? value, string parameterName)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (value.Length == 0) throw new ArgumentException("原因不可為空字串。", parameterName);
        return value;
    }
}
