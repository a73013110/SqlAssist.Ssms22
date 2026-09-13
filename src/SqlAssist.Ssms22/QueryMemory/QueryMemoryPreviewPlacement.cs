using System;
using System.Windows;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Settings;
using SqlAssist.Ssms22.Preview;

namespace SqlAssist.Ssms22.QueryMemory;

internal static class QueryMemoryPreviewPlacement
{
    private const string Collection = "SqlAssist\\QueryMemoryPreview";
    public static void Restore(Window window, IServiceProvider services)
    {
        SqlAssistPlatformGuard.Probe("讀取查詢記憶預覽位置", () =>
        {
            var store = new ShellSettingsManager(services).GetWritableSettingsStore(SettingsScope.UserSettings);
            if (!store.CollectionExists(Collection)) return;
            window.Width = Math.Max(window.MinWidth, store.GetInt32(Collection, "Width", 900));
            window.Height = Math.Max(window.MinHeight, store.GetInt32(Collection, "Height", 620));
            window.Left = store.GetInt32(Collection, "Left", 100);
            window.Top = store.GetInt32(Collection, "Top", 100);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
        });
        // HWND 已建立後才有目前螢幕的 DPI；離線螢幕的位置退回最近工作區。
        window.Loaded += (_, _) => SqlAssistPlatformGuard.Probe("限制查詢記憶預覽邊界", () =>
        {
            var device = NativeScreen.GetTransformToDevice(window);
            var origin = device.Transform(new Point(window.Left, window.Top));
            if (NativeScreen.TryGetWorkArea(origin) is not { } pixels) return;
            var inverse = NativeScreen.GetTransformFromDevice(window);
            var area = new Rect(inverse.Transform(pixels.TopLeft), inverse.Transform(pixels.BottomRight));
            window.MinWidth = Math.Min(window.MinWidth, area.Width);
            window.MinHeight = Math.Min(window.MinHeight, area.Height);
            window.Width = Math.Min(window.Width, area.Width);
            window.Height = Math.Min(window.Height, area.Height);
            window.Left = Math.Max(area.Left, Math.Min(window.Left, area.Right - window.Width));
            window.Top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.Height));
        });
    }

    public static void Save(Window window, IServiceProvider services) => SqlAssistPlatformGuard.Probe("保存查詢記憶預覽位置", () =>
    {
        var bounds = window.RestoreBounds;
        if (bounds.IsEmpty || double.IsNaN(bounds.Left)) return;
        var store = new ShellSettingsManager(services).GetWritableSettingsStore(SettingsScope.UserSettings);
        store.CreateCollection(Collection);
        store.SetInt32(Collection, "Width", (int)bounds.Width);
        store.SetInt32(Collection, "Height", (int)bounds.Height);
        store.SetInt32(Collection, "Left", (int)bounds.Left);
        store.SetInt32(Collection, "Top", (int)bounds.Top);
    });
}
