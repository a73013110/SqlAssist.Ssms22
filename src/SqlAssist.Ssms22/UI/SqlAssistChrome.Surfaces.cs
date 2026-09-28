using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 浮在編輯器上的表面（通知島、浮動預覽）共用的外觀；進出場節奏在 <see cref="SurfaceMotion"/>。
/// </summary>
/// <remarks>
/// 各自寫一份的話，兩個表面的圓角與影子遲早不一樣，而它們正是使用者會同時看到的兩個浮層。
/// </remarks>
internal static partial class SqlAssistChrome
{
    /// <summary>通知島展開的面板與浮動預覽的圓角。</summary>
    internal const double FloatingSurfaceRadius = 12d;

    /// <summary>浮層上的叉號；通知島、提醒卡與其他浮層的關閉鈕畫同一筆。</summary>
    internal const string CrossGlyph = "M1,1 L11,11 M11,1 L1,11";

    /// <summary>
    /// 掛上或拿掉浮層的單層柔影；高對比一律不畫。
    /// </summary>
    /// <remarks>
    /// 柔影掛在只有底色的那一層，不掛在內容上：掛在內容上時文字也會帶著一圈模糊。
    /// 依 DPI 給點陣快取倍率，150%／200% 才不會糊；捲動或變形之外的影格只是搬一張圖。
    /// </remarks>
    internal static void SetSurfaceShadow(UIElement layer, bool on)
    {
        layer.Effect = on && !SystemParameters.HighContrast
            ? new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.14 }
            : null;
        UpdateSurfaceShadowCache(layer);
    }

    /// <summary>換 DPI 時重建柔影的點陣快取。</summary>
    internal static void UpdateSurfaceShadowCache(UIElement layer) =>
        layer.CacheMode = layer.Effect is null ? null
            : new BitmapCache { RenderAtScale = VisualTreeHelper.GetDpi(layer).DpiScaleX, SnapsToDevicePixels = true };
}
