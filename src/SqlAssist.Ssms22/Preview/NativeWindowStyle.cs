using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 請 DWM 幫浮動視窗把四個角磨圓。
/// </summary>
/// <remarks>
/// 圓角在 WPF 這一層做不到。空間保留機制建立的 <see cref="System.Windows.Controls.Primitives.Popup"/>
/// 是平台自己 new 出來的，我們拿不到它、也就無法在它開啟之前設定
/// <c>AllowsTransparency</c>；沒有透明度，<c>CornerRadius</c> 只會在角落留下
/// 承載視窗的方形底色。
///
/// 改請作業系統做：Windows 11 的 <c>DWMWA_WINDOW_CORNER_PREFERENCE</c> 會把整個
/// 視窗區域裁成圓角，方形底色連同角落的內容一起被裁掉，成本落在合成器上。
/// Windows 10 沒有這個屬性，呼叫會回傳失敗的 HRESULT——那時就維持方角，
/// 呼叫端也不會把 <c>CornerRadius</c> 打開，不至於露出黑色的三角形。
/// </remarks>
internal static class NativeWindowStyle
{
    /// <summary>DWM 圓角偏好；Windows 11 (build 22000) 之後才存在。</summary>
    private const int WindowCornerPreference = 33;

    /// <summary>依視窗類型自動選一個圓角半徑，一般視窗是 8。</summary>
    private const int PreferenceRound = 2;

    /// <summary>與 <see cref="PreferenceRound"/> 對應的半徑，WPF 這一側要畫成一樣才貼合。</summary>
    public const double CornerRadius = 8;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int size);

    /// <summary>
    /// 把某個元素所在的承載視窗磨圓；成功才回傳 true。
    /// </summary>
    /// <remarks>
    /// 刻意不先判斷 Windows 版本。<see cref="Environment.OSVersion"/> 在沒有相容性
    /// 資訊清單的行程裡會謊報成 Windows 8，而 DWM 自己就會用 HRESULT 告訴我們
    /// 這個屬性存不存在——直接問它比猜版本可靠。
    /// </remarks>
    public static bool TryRoundCorners(Visual visual)
    {
        try
        {
            if (PresentationSource.FromVisual(visual) is not HwndSource { Handle: var handle } ||
                handle == IntPtr.Zero)
            {
                return false;
            }

            var preference = PreferenceRound;
            return DwmSetWindowAttribute(handle, WindowCornerPreference, ref preference, sizeof(int)) == 0;
        }
        catch (Exception exception)
        {
            // dwmapi.dll 一定在，但組合服務停用時呼叫仍可能失敗；方角不值得中斷預覽。
            SqlAssistDiagnostics.Write($"設定視窗圓角失敗：{exception.Message}");
            return false;
        }
    }
}
