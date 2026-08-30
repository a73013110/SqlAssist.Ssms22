using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;
using Microsoft.VisualStudio.TextManager.Interop;
using SqlAssist.Ssms22;

namespace SqlAssist.Ssms22.Settings;

/// <summary>
/// 擋掉 SSMS 內建 T-SQL IntelliSense 打字時自動彈出的那份建議清單，其餘功能不動。
/// </summary>
/// <remarks>
/// 為什麼不是「關掉內建 IntelliSense」：SSMS 的 <c>RadLangSvc.registration.json</c>
/// 把 <c>underlineErrors</c>（紅色錯誤波浪線）與 <c>autoOutlining</c> 都以
/// <c>enableWhen</c> 掛在 <c>enableIntellisense</c> 底下，關掉總開關等於連錯誤檢查
/// 一起關掉——而錯誤檢查是這個擴充完全沒有提供的東西。
///
/// 能只擋清單，是因為舊版語言服務決定「要不要把清單畫出來」的那一行讀的是另一個旗標。
/// <c>Microsoft.VisualStudio.Package.Source.HandleCompletionResponse</c> 只在
/// <c>AutoListMembers || reason == CompleteWord || reason == DisplayMemberList</c>
/// 成立時才呼叫 <c>completionSet.Init</c>。那個方法是 internal 且非虛擬，
/// RadLangSvc 覆寫不了；它的 <c>LanguagePreferences</c> 子類別
/// （<c>SqlIntelliSenseSettings</c>）也沒有覆寫 <c>AutoListMembers</c>。
/// 波浪線則走另一條路——<c>Source.OnIdle</c> 到 <c>BeginParse(ParseReason.Check)</c>
/// ——完全不看這個旗標。
///
/// 所以 <c>fAutoListMembers = 0</c> 的效果剛好是：打字時不再自動彈出，
/// 使用者自己按 Ctrl+Space／Ctrl+J 仍然叫得出來，錯誤檢查完好。順帶收掉的還有
/// <c>RadLangSvc.Source.OnCommand</c> 裡「Backspace／Delete 時重新篩選舊版清單」
/// 那一段——它只在清單顯示中才執行。
///
/// <c>LanguagePreferences</c> 實作 <c>IVsTextManagerEvents2</c>，寫下去立刻生效，
/// 不必重開查詢視窗（<c>enableIntellisense</c> 是在 <c>RadLangSvc.Source</c> 的建構式裡
/// 抓進欄位的，那個才需要重開）。
///
/// 這是唯一一個作用在<b>擴充之外</b>的設定，因此不能只放著等人來讀，要一直推：
/// 套件載入、每次建立 SQL 編輯器、設定變更時各重套一次。SSMS 22 的設定 UI 沒有
/// 暴露這個旗標，唯一的寫入者就是這裡，所以「還原」就是寫回 1——
/// <c>RadLangSvc.pkgdef</c> 註冊的預設值就是 <c>ShowCompletion=1</c>。
/// 套件卸載時會還原，讓擴充不留痕跡。
/// </remarks>
internal static class NativeMemberList
{
    /// <summary>
    /// SSMS T-SQL 語言服務的識別碼，取自 <c>RadLangSvc.pkgdef</c> 的
    /// <c>Languages\Language Services\SQL</c>。
    /// </summary>
    /// <remarks>
    /// 只當備援：實際值優先向殼層問一次，SSMS 換掉語言服務時跟得上。
    /// 兩邊都不對時，寫進去的偏好沒有人讀，不會有副作用。
    /// </remarks>
    private static readonly Guid FallbackLanguageService = new("c4d96929-a9b0-42cc-b3e0-adac0435d7f2");

    /// <summary>殼層設定存放區裡登記語言服務識別碼的位置。</summary>
    private const string LanguageServiceCollection = @"Languages\Language Services\SQL";

    private static readonly object Gate = new();

    private static IServiceProvider? _serviceProvider;
    private static Guid? _languageService;

    /// <summary>記下取得殼層服務的入口；重複呼叫只有第一次生效。</summary>
    public static void Initialize(IServiceProvider serviceProvider)
    {
        lock (Gate)
        {
            _serviceProvider ??= serviceProvider;
        }
    }

    /// <summary>
    /// 依目前設定重新套用一次。
    /// </summary>
    /// <remarks>
    /// 呼叫端不必在 UI 執行緒上：設定變更的訂閱回呼可能來自任何執行緒，
    /// 而 <c>IVsTextManager</c> 是殼層服務。已經在 UI 執行緒上時會直接接下去執行。
    /// </remarks>
    public static void ApplyFromSettings()
    {
        if (_serviceProvider is null)
        {
            return;
        }

        SqlAssistPlatformGuard.BeginProbe("套用內建建議清單設定", async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Apply(ShouldSuppress());
        });
    }

    /// <summary>把設定還原成 SSMS 原本的樣子。</summary>
    public static void Restore()
    {
        Apply(suppress: false);
    }

    /// <summary>
    /// 內建的自動建議清單現在是不是被擋著。
    /// </summary>
    /// <returns>問不到殼層時為 <c>null</c>；那不代表它沒被擋。</returns>
    public static bool? TryGetSuppressed()
    {
        return SqlAssistPlatformGuard.Probe<bool?>(
            "讀取內建建議清單狀態",
            () => TryReadPreferences(out _, out var preferences)
                ? preferences[0].fAutoListMembers == 0
                : null,
            fallback: null);
    }

    /// <remarks>
    /// 關掉 SqlAssist、或關掉它的建議清單的人要的是「回到 SSMS 原本的樣子」，
    /// 不是「兩邊都沒有清單」。這條規則只寫在這裡，三個呼叫端都走這一份。
    /// </remarks>
    private static bool ShouldSuppress()
    {
        var settings = SqlAssistSettingsStore.Current;

        return settings.Enabled && settings.SuggestionsEnabled && settings.SuppressNativeMemberList;
    }

    private static void Apply(bool suppress)
    {
        SqlAssistPlatformGuard.Probe("套用內建建議清單設定", () =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!TryReadPreferences(out var textManager, out var preferences))
            {
                return;
            }

            // 每建立一個 SQL 編輯器就會走到這裡，狀態相同時不要寫——
            // 寫入會廣播一次偏好變更通知，讓每個語言服務重讀一次。
            if ((preferences[0].fAutoListMembers == 0) == suppress)
            {
                return;
            }

            preferences[0].fAutoListMembers = suppress ? 0u : 1u;

            if (ErrorHandler.Failed(textManager.SetUserPreferences2(null, null, preferences, null)))
            {
                SqlAssistDiagnostics.WriteAlways("無法變更 SSMS 內建建議清單的設定");
                return;
            }

            SqlAssistDiagnostics.WriteAlways(suppress
                ? "已擋掉 SSMS 內建的自動建議清單；錯誤波浪線與大綱不受影響"
                : "已還原 SSMS 內建的自動建議清單");
        });
    }

    private static bool TryReadPreferences(
        out IVsTextManager2 textManager,
        out LANGPREFERENCES2[] preferences)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        textManager = null!;
        preferences = null!;

        if (_serviceProvider?.GetService(typeof(SVsTextManager)) is not IVsTextManager2 manager)
        {
            return false;
        }

        // guidLang 是輸入，其餘欄位由殼層填回來；整份讀回來再寫回去，
        // 只改一個欄位，才不會把使用者其他的語言偏好一起改掉。
        var current = new[] { new LANGPREFERENCES2 { guidLang = ResolveLanguageService() } };

        if (ErrorHandler.Failed(manager.GetUserPreferences2(null, null, current, null)))
        {
            return false;
        }

        textManager = manager;
        preferences = current;
        return true;
    }

    private static Guid ResolveLanguageService()
    {
        lock (Gate)
        {
            return _languageService ??= ReadLanguageService();
        }
    }

    private static Guid ReadLanguageService()
    {
        if (_serviceProvider is not { } serviceProvider)
        {
            return FallbackLanguageService;
        }

        var declared = SqlAssistPlatformGuard.Probe(
            "讀取 SQL 語言服務識別碼",
            () =>
            {
                var store = new ShellSettingsManager(serviceProvider)
                    .GetReadOnlySettingsStore(SettingsScope.Configuration);

                return store.CollectionExists(LanguageServiceCollection)
                    ? store.GetString(LanguageServiceCollection, string.Empty, string.Empty)
                    : string.Empty;
            },
            fallback: string.Empty);

        if (Guid.TryParse(declared, out var value))
        {
            return value;
        }

        SqlAssistDiagnostics.Write($"SQL 語言服務識別碼改用內建值：{FallbackLanguageService}");
        return FallbackLanguageService;
    }
}
