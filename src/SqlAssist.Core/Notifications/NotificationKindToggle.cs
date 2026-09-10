using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Core.Notifications;

/// <summary>一個種類的顯示開關：moniker、預設值與設定頁標題。</summary>
public sealed class NotificationKindToggle
{
    private NotificationKindToggle(NotificationKind kind, string moniker, bool enabledByDefault, string title)
    {
        Kind = kind;
        Moniker = moniker;
        EnabledByDefault = enabledByDefault;
        Title = title;
    }

    public NotificationKind Kind { get; }

    /// <summary>對應的設定 moniker；每一個種類都有一個，註冊檔逐項寫。</summary>
    public string Moniker { get; }

    /// <summary>註冊檔那一項該寫的 <c>default</c>，也是這一格核取方塊的初始狀態。</summary>
    public bool EnabledByDefault { get; }

    public string Title { get; }

    /// <summary>
    /// 種類開關的唯一出處：新增一個種類只動這張表與註冊檔兩處。
    /// </summary>
    /// <remarks>
    /// 表驅動而不是逐項屬性：屬性版本要同時動 POCO、moniker 常數、讀取端與可見度的
    /// switch，四處各漏一次都不會有編譯錯誤。moniker 字面值也放在這裡而不是
    /// <c>SqlAssistMonikers</c>，那邊改成把這張表併進 <c>All</c>，字串仍然只寫一次。
    ///
    /// 每一個種類都有開關，包括永遠由使用者觸發的那幾類：使用者按下去的事跨得過
    /// 詳細度門檻，但跨不過種類開關，所以每一格都管得住自己那一類。只給一半的話，
    /// 剩下那幾類會像是壞掉的。
    /// </remarks>
    public static readonly IReadOnlyList<NotificationKindToggle> All = new[]
    {
        new NotificationKindToggle(NotificationKind.Metadata, "sqlAssist.notifications.metadata", true, "中繼資料載入"),
        new NotificationKindToggle(NotificationKind.Completion, "sqlAssist.notifications.completion", false, "建議清單"),
        new NotificationKindToggle(NotificationKind.Analysis, "sqlAssist.notifications.analysis", false, "語法與區塊分析"),
        new NotificationKindToggle(NotificationKind.Preview, "sqlAssist.notifications.preview", false, "物件提示與結構預覽"),
        new NotificationKindToggle(NotificationKind.Package, "sqlAssist.notifications.package", false, "初始化與連線"),
        new NotificationKindToggle(NotificationKind.Editing, "sqlAssist.notifications.editing", true, "編輯與展開"),
        new NotificationKindToggle(NotificationKind.Navigation, "sqlAssist.notifications.navigation", true, "移至定義"),
        new NotificationKindToggle(NotificationKind.Results, "sqlAssist.notifications.results", true, "結果格線"),
        new NotificationKindToggle(NotificationKind.Snippets, "sqlAssist.notifications.snippets", true, "程式碼片段"),
        new NotificationKindToggle(NotificationKind.Settings, "sqlAssist.notifications.settings", true, "設定"),
        // 漏分類要看得見，不能沿用預設隱藏的種類。
        new NotificationKindToggle(NotificationKind.Unclassified, "sqlAssist.notifications.unclassified", true, "未分類"),
    };

    private static readonly Dictionary<NotificationKind, NotificationKindToggle> ByKind =
        All.ToDictionary(toggle => toggle.Kind);

    public static NotificationKindToggle For(NotificationKind kind) => ByKind[kind];
}
