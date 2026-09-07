using System;
using System.Windows.Media;
using SqlAssist.Core.Settings;
using Forms = System.Windows.Forms;

namespace SqlAssist.Ssms22.UI;

/// <summary>使用 Windows 原生選色器，不維護第二套色盤或設定儲存區。</summary>
internal static class SqlColorPicker
{
    public static string? Pick(IntPtr owner, string? preference, Color fallback)
    {
        var color = SqlColorPreference.TryParseRgb(preference, out var rgb)
            ? System.Drawing.Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255)
            : System.Drawing.Color.FromArgb(fallback.R, fallback.G, fallback.B);
        using var dialog = new Forms.ColorDialog { Color = color, FullOpen = true, AnyColor = true, SolidColorOnly = true };
        if (dialog.ShowDialog(new Owner(owner)) != Forms.DialogResult.OK) return null;
        return $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    private sealed class Owner : Forms.IWin32Window
    {
        public Owner(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }
}
