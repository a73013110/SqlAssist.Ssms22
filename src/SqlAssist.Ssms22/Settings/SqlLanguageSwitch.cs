using System;
using System.Globalization;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Settings;

namespace SqlAssist.Ssms22.Settings;

/// <summary>
/// 把語言設定套到 <see cref="SqlText"/>，並把切換轉到 UI 執行緒給顯示中的介面。
/// </summary>
/// <remarks>
/// 宿主的介面文化只在 UI 執行緒上問得到（<see cref="IUIHostLocale"/>），設定變更卻可能從任何
/// 執行緒通知，所以第一次接上時問一次記下來；SSMS 換介面語言要重新啟動，記下來的值不會過時。
/// 不用 <see cref="CultureInfo.CurrentUICulture"/>：那是執行緒的狀態，背景執行緒上不保證等於宿主。
///
/// <see cref="SqlText.Changed"/> 在呼叫端的執行緒上同步發出，一個處理常式丟例外就擋掉後面的。
/// 介面一律改接這裡的 <see cref="Changed"/>：保證在 UI 執行緒上，每個處理常式各自走 Guard。
/// </remarks>
internal static class SqlLanguageSwitch
{
    private static CultureInfo? s_hostCulture;
    private static Dispatcher? s_dispatcher;

    /// <summary>介面語言換了；在 UI 執行緒上發出。訂閱者要在關閉時解除訂閱。</summary>
    public static event EventHandler? Changed;

    /// <summary>「跟隨 SSMS」時用的宿主介面文化；還沒接上之前是目前執行緒的。</summary>
    public static CultureInfo HostCulture => s_hostCulture ?? CultureInfo.CurrentUICulture;

    /// <summary>在 UI 執行緒上呼叫；重複呼叫只有第一次會問宿主。</summary>
    public static void Initialize(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (s_dispatcher is not null) return;
        s_dispatcher = Dispatcher.CurrentDispatcher;
        s_hostCulture = SqlAssistPlatformGuard.Run(
            "讀取 SSMS 介面語言",
            () => ReadHostCulture(serviceProvider),
            fallback: CultureInfo.CurrentUICulture);
        SqlText.Changed += OnTextChanged;
    }

    public static void Shutdown()
    {
        SqlText.Changed -= OnTextChanged;
        s_dispatcher = null;
    }

    /// <summary>依設定決定語言；與目前相同時什麼都不做。</summary>
    public static void Apply(SqlAssistSettings settings)
    {
        var language = settings.Language ?? SqlLanguage.Match(HostCulture);
        if (SqlText.SetLanguage(language))
            SqlAssistDiagnostics.WriteAlways($"介面語言：{language.Name}（設定 {settings.Language?.Name ?? "auto"}，SSMS {HostCulture.Name}）");
    }

    private static CultureInfo ReadHostCulture(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (serviceProvider.GetService(typeof(SUIHostLocale)) is IUIHostLocale locale &&
            ErrorHandler.Succeeded(locale.GetUILocale(out var lcid)))
            return CultureInfo.GetCultureInfo((int)lcid);

        SqlAssistDiagnostics.WriteAlways("取不到 SSMS 介面語言，「跟隨 SSMS」改看目前執行緒的文化");
        return CultureInfo.CurrentUICulture;
    }

    private static void OnTextChanged(object? sender, EventArgs args)
    {
        if (s_dispatcher is not { } dispatcher) return;
        if (dispatcher.CheckAccess()) Raise();
        else dispatcher.BeginInvoke(new Action(Raise));
    }

    private static void Raise()
    {
        if (Changed is not { } handlers) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            SqlAssistPlatformGuard.Run("套用介面語言", () => handler(null, EventArgs.Empty));
    }
}
