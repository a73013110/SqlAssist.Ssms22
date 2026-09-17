using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlAssist.Ssms22.Notifications;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 所有 <c>DialogWindow</c> 共用的殼層設定；版面元件在 <see cref="SqlAssistChrome"/> 的對話框分部。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlAssistChrome"/> 分開，是因為這裡要向 SSMS 取主題資源，
/// 而 Chrome 必須能單獨編進 WPF 測試。
/// </remarks>
internal static class SqlAssistDialogs
{
    /// <summary>標題、尺寸、主題資源、介面字型、不進工作列、置中於擁有者。</summary>
    /// <remarks>擁有者由呼叫端決定：有 SSMS 殼層的從 <c>IVsUIShell</c> 取，巢狀確認框直接指定父視窗。</remarks>
    /// <param name="height">null 表示依內容決定高度（<see cref="SizeToContent.Height"/>）。</param>
    public static void Configure(Window window, string title, double width, double? height, double minWidth = 0, double minHeight = 0)
    {
        VsThemeBrushes.Apply(window);
        window.Title = title;
        window.Width = width;
        if (height is { } value) window.Height = value;
        else window.SizeToContent = SizeToContent.Height;
        window.MinWidth = minWidth;
        window.MinHeight = minHeight;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.FontFamily = SqlAssistChrome.InterfaceFont;
        window.FontSize = SqlAssistChrome.DefaultMetrics.Body;
        window.SetResourceReference(Control.BackgroundProperty, ThemeBrush.WindowBackground);
        window.SetResourceReference(Control.ForegroundProperty, ThemeBrush.WindowForeground);
        // 字距交給排版而不是像素對齊，小字級下才不會忽寬忽窄。
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Ideal);
        // 對話框開著時完成的工作也要看得到；關閉時自動放手，卡片交給下一個宿主。
        NotificationWindowHost.Register(window);
    }
}
