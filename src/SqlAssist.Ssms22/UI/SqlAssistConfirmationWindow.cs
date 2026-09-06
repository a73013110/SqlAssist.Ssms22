using System.Windows;
using Microsoft.VisualStudio.PlatformUI;

namespace SqlAssist.Ssms22.UI;

/// <summary>需要明確動作名稱的確認框；一般訊息仍交給 SSMS 原生訊息框。</summary>
internal sealed class SqlAssistConfirmationWindow : DialogWindow
{
    private SqlAssistConfirmationWindow(Window owner, string title, string message, string detail, string action)
    {
        VsThemeBrushes.Apply(this);
        Owner = owner;
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = SqlAssistChrome.InterfaceFont;
        FontSize = SqlAssistChrome.DefaultMetrics.Body;
        SetResourceReference(BackgroundProperty, ThemeBrush.WindowBackground);
        SetResourceReference(ForegroundProperty, ThemeBrush.WindowForeground);
        Content = SqlAssistChrome.CreateConfirmationContent(message, detail, action, out var confirm, out var cancel);
        confirm.Click += (_, _) => SqlAssistPlatformGuard.Run("確認片段操作", () => DialogResult = true);
        // IsDefault 不等於初始焦點；明確聚焦取消，避免第一個 Enter 誤觸確認。
        Loaded += (_, _) => SqlAssistPlatformGuard.Run("聚焦取消操作", () => cancel.Focus());
    }

    public static bool Confirm(Window owner, string title, string message, string detail, string action) =>
        new SqlAssistConfirmationWindow(owner, title, message, detail, action).ShowModal() == true;
}
