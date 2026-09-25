using System;
using System.ComponentModel;
using System.Threading;

namespace SqlAssist.Core.Localization;

/// <summary>目前的介面語言，以及產生的文字類別取值的入口。</summary>
/// <remarks>
/// 不動 <see cref="Thread.CurrentUICulture"/>：那是 SSMS 的狀態，改了連宿主的介面也會跟著變。
/// 宿主在設定載入與變更時呼叫 <see cref="SetLanguage"/>；需要固定語言的一段程式碼（測試、
/// 要寫進檔案的固定格式）用 <see cref="Use"/>，只影響同一條非同步流程，平行的測試互不干擾。
/// </remarks>
public static class SqlText
{
    private static readonly AsyncLocal<SqlLanguage?> s_scoped = new();
    private static SqlLanguage s_current = SqlLanguage.Source;

    /// <summary>介面語言換了。已經產生的文字不會自己變，顯示中的介面要重新取值。</summary>
    public static event EventHandler? Changed;

    public static SqlLanguage Current => s_scoped.Value ?? Volatile.Read(ref s_current);

    /// <summary>切換全域語言；與目前相同時不發事件，回傳是否真的換了。</summary>
    public static bool SetLanguage(SqlLanguage language)
    {
        if (language is null)
        {
            throw new ArgumentNullException(nameof(language));
        }

        if (ReferenceEquals(Interlocked.Exchange(ref s_current, language), language))
        {
            return false;
        }

        Changed?.Invoke(null, EventArgs.Empty);
        return true;
    }

    /// <summary>在 <c>using</c> 範圍內（含其中 await 的後續）改用指定語言。</summary>
    public static IDisposable Use(SqlLanguage language)
    {
        var previous = s_scoped.Value;
        s_scoped.Value = language ?? throw new ArgumentNullException(nameof(language));
        return new Scope(previous);
    }

    /// <summary>不在句子裡的數字（統計格、徽章、分頁數量）：依目前語言的文化加千分位。</summary>
    /// <remarks>
    /// 句子裡的數字直接交給產生的方法、在 .resjson 寫 <c>{count:N0}</c>；這裡給沒有句子可放的地方。
    /// 不用 <see cref="System.Globalization.CultureInfo.CurrentCulture"/>：那是 SSMS 的文化，介面語言可以與它不同。
    /// </remarks>
    public static string Number(long value) => value.ToString("N0", Current.Culture);

    /// <summary>產生的程式碼專用：沒有佔位符的一句。</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static string Pick(string[] values) => values[Current.Index];

    /// <summary>產生的程式碼專用：依目前語言的文化填入佔位符。</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static string Format(string[] values, params object?[] arguments)
    {
        var language = Current;
        return string.Format(language.Culture, values[language.Index], arguments);
    }

    private sealed class Scope : IDisposable
    {
        private readonly SqlLanguage? _previous;

        public Scope(SqlLanguage? previous)
        {
            _previous = previous;
        }

        public void Dispose() => s_scoped.Value = _previous;
    }
}
