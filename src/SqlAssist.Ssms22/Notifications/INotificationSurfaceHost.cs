using System;
using System.Windows;
using System.Windows.Threading;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Notifications;

/// <summary>宿主的種類；同樣作用中時 SQL 編輯區優先。</summary>
internal enum NotificationSurfaceHostKind { Editor, Window }

/// <summary>宿主離使用者的注意力有多近。</summary>
internal enum NotificationSurfaceHostActivity
{
    /// <summary>不接卡片。</summary>
    Inactive,

    /// <summary>最後用過的 SQL 編輯區：焦點在物件總管或別的應用程式時卡片仍留在那裡。</summary>
    Recent,

    /// <summary>鍵盤焦點在裡面，或它是作用中的視窗。</summary>
    Focused,
}

/// <summary>
/// 能掛通知卡片的地方：SQL 編輯區的 adornment 層，或 SqlAssist 自己的 WPF 視窗。
/// </summary>
/// <remarks>
/// 宿主只回報「我能不能掛、我離焦點多近、我變了」與自己的座標；何時顯示、掛到誰身上、
/// 計時器與設定、主題、通知的訂閱全在 <see cref="NotificationSurfaceController"/>。
/// 每個宿主各持有一份的版本，N 個編輯區就是 N 個計時器與 N 份全域訂閱。
///
/// 不做 SSMS 主視窗疊層：結果格線是 WinForms／HWND，會蓋住任何 WPF 圖層。
/// 也不做 Popup 或獨立視窗：置頂、最小化、DPI 與搶焦點都要自己處理。
///
/// 成員只在 <see cref="Dispatcher"/> 的執行緒上呼叫。
/// </remarks>
internal interface INotificationSurfaceHost : IDisposable
{
    NotificationSurfaceHostKind Kind { get; }

    Dispatcher Dispatcher { get; }

    /// <summary>看得見，而且現在就掛得上去。</summary>
    bool IsVisible { get; }

    NotificationSurfaceHostActivity Activity { get; }

    /// <summary>卡片可用的範圍；座標與 <see cref="System.Windows.Controls.Canvas"/> 的定位相同。</summary>
    Rect Viewport { get; }

    /// <summary>把卡片掛上去。</summary>
    /// <param name="removed">宿主自己把卡片收掉時（編輯器關閉、換版面）要呼叫。</param>
    bool Mount(NotificationCard card, Action removed);

    void Unmount(NotificationCard card);

    /// <summary>捲動或尺寸變了；只需要重新定位。</summary>
    event EventHandler? ViewportChanged;

    /// <summary>可見度或焦點變了；可能換宿主。</summary>
    event EventHandler? StateChanged;
}
