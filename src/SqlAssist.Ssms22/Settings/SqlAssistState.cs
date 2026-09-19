using System;
using System.Globalization;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Settings;

namespace SqlAssist.Ssms22.Settings;

/// <summary>
/// 跨工作階段記住的內部狀態：上次檢查更新的時間與 ETag、首次擷取的通知說過了沒有、
/// SQL Search 上次用的比對方式。
/// </summary>
/// <remarks>
/// 刻意不放進 Unified Settings。那份是使用者刻意調整、會漫遊同步、也會出現在設定頁上的
/// 偏好；這裡的每一項都沒有可調的東西，做成設定只是在問一個使用者沒有依據的問題。
/// 視窗尺寸走同一個存放區，見 <see cref="PreviewWindowState"/>。
///
/// 讀不到存放區時每一項都回退成「沒有記錄」：最壞的結果是自動檢查多跑一次、
/// 首次擷取的說明多出現一次，不是功能停擺。
/// </remarks>
internal static class SqlAssistState
{
    private const string Collection = @"SqlAssist\State";
    private const string UpdateCheckedAtProperty = "UpdateCheckedAt";
    private const string UpdateETagProperty = "UpdateETag";
    private const string UpdateTagProperty = "UpdateTag";
    private const string SqlMemoryCaptureNoticeProperty = "SqlMemoryCaptureNotice";
    private const string SearchMatchStateProperty = "SearchMatchState";

    private static WritableSettingsStore? _store;
    private static bool _resolved;

    /// <summary>取得存放區。必須在 UI 執行緒上呼叫；服務還沒就緒時下一次再試。</summary>
    public static void Initialize(IServiceProvider serviceProvider)
    {
        if (_resolved || serviceProvider is null) return;

        var store = SqlAssistPlatformGuard.Probe<WritableSettingsStore?>(
            "取得 SqlAssist 狀態存放區",
            () => new ShellSettingsManager(serviceProvider).GetWritableSettingsStore(SettingsScope.UserSettings),
            fallback: null);

        if (store is null) return;

        _store = store;
        _resolved = true;
    }

    /// <summary>上一次自動檢查更新的時間；沒有記錄時是 <see cref="DateTimeOffset.MinValue"/>。</summary>
    public static DateTimeOffset UpdateCheckedAt
    {
        get => Read(UpdateCheckedAtProperty) is { Length: > 0 } text &&
               DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind, out var value)
            ? value
            : DateTimeOffset.MinValue;
        set => Write(UpdateCheckedAtProperty, value.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>上一次回應的 ETag；帶著它再問可以拿 304，不吃未驗證配額的完整回應。</summary>
    public static string UpdateETag
    {
        get => Read(UpdateETagProperty);
        set => Write(UpdateETagProperty, value);
    }

    /// <summary>ETag 對應的那個發行 tag；收到 304 時沿用它，不必再問一次。</summary>
    public static string UpdateTag
    {
        get => Read(UpdateTagProperty);
        set => Write(UpdateTagProperty, value);
    }

    /// <summary>「SQL Memory 開始擷取」那一則說過了沒有；每台電腦只說一次。</summary>
    public static bool SqlMemoryCaptureNoticeShown
    {
        get => Read(SqlMemoryCaptureNoticeProperty) == "1";
        set => Write(SqlMemoryCaptureNoticeProperty, value ? "1" : "0");
    }

    /// <summary>SQL Search 上次用的比對方式；沒有記錄時是空字串。</summary>
    /// <remarks>
    /// 格式由 <c>SqlSearchBrowserModel.MatchStateToken</c> 決定，這一層只存字串——
    /// 拆成三個屬性的話，只有其中一個寫成功的那一次會半套還原。
    /// 它不進 Unified Settings：工具列上隨手切的狀態不是設定頁上的偏好，
    /// 每按一下就提交一次設定變更並廣播通知，理由與 <see cref="PreviewWindowState"/> 相同。
    /// </remarks>
    public static string SearchMatchState
    {
        get => Read(SearchMatchStateProperty);
        set => Write(SearchMatchStateProperty, value);
    }

    private static string Read(string property)
    {
        if (_store is not { } store) return string.Empty;

        return SqlAssistPlatformGuard.Probe(
            $"讀取 SqlAssist 狀態 {property}",
            () => store.CollectionExists(Collection) ? store.GetString(Collection, property, string.Empty) : string.Empty,
            string.Empty);
    }

    private static void Write(string property, string value)
    {
        if (_store is not { } store) return;

        // 記不住只代表下一次重做一遍，不影響這一次的結果。
        SqlAssistPlatformGuard.Run($"儲存 SqlAssist 狀態 {property}", () =>
        {
            store.CreateCollection(Collection);
            store.SetString(Collection, property, value);
        });
    }
}
