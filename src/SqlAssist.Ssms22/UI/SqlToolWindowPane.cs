using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 本擴充停駐工具窗的共同基底：內容一律放進 <see cref="Host"/>，開啟一律走 <see cref="Open{TWindow}"/>。
/// </summary>
/// <remarks>
/// 下面兩件事都是殼層自動隱藏的行為造成的，與內容無關；新的工具窗繼承這裡就一併得到，不必各自記得。
/// </remarks>
public abstract class SqlToolWindowPane : ToolWindowPane
{
    private bool _relayoutPending;

    protected SqlToolWindowPane() : base(null)
    {
        Content = Host;
        Host.IsVisibleChanged += OnHostIsVisibleChanged;
    }

    /// <summary>工具窗內容的宿主；換內容就換它的 <see cref="ContentControl.Content"/>。</summary>
    protected ContentControl Host { get; } = new();

    /// <summary>
    /// 打開工具窗：等這一輪輸入收完才叫殼層顯示，擺好之後才交內容，最後焦點落在窗裡。
    /// </summary>
    /// <remarks>
    /// 自動隱藏的工具窗被 <c>Show()</c> 叫出來只是一片滑出面板，鍵盤焦點一離開就收回。從浮動預覽、
    /// 通知這類自製浮窗按下去時，浮窗正要收起，殼層隨後會把啟用與焦點還給編輯器；當場顯示的話面板
    /// 剛滑出就被收回。所以一律排到 <see cref="DispatcherPriority.Background"/>（比輸入低）再顯示，
    /// 並確保焦點在內容裡。
    ///
    /// 使用者按下命令才走到這裡，失敗要看得見，所以不交給 Guard 吞掉。套件一律從服務提供者載入：
    /// 浮動預覽是編輯器那一端 MEF 建出來的，手上沒有套件執行個體。
    /// </remarks>
    /// <param name="present">工具窗已顯示後換上內容；要指定焦點落點也在這裡做，沒指定就落在第一個可聚焦的元素。</param>
    protected static void Open<TWindow>(
        IServiceProvider serviceProvider,
        string missingMessage,
        string failureTitle,
        Action<TWindow> present)
        where TWindow : SqlToolWindowPane
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => OpenNow(serviceProvider, missingMessage, failureTitle, present)));
    }

    private static void OpenNow<TWindow>(
        IServiceProvider serviceProvider,
        string missingMessage,
        string failureTitle,
        Action<TWindow> present)
        where TWindow : SqlToolWindowPane
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (serviceProvider.GetService(typeof(SVsShell)) is not IVsShell shell)
                throw new InvalidOperationException(missingMessage);
            var packageGuid = new Guid(SqlAssistPackage.PackageGuidString);
            ErrorHandler.ThrowOnFailure(shell.LoadPackage(ref packageGuid, out var loaded));
            if (loaded is not SqlAssistPackage package ||
                package.FindToolWindow(typeof(TWindow), 0, true) is not TWindow window ||
                window.Frame is not IVsWindowFrame frame)
                throw new InvalidOperationException(missingMessage);
            ErrorHandler.ThrowOnFailure(frame.Show());
            present(window);
            if (!window.Host.IsKeyboardFocusWithin)
                window.Host.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
        catch (Exception error)
        {
            VsShellUtilities.ShowMessageBox(serviceProvider, error.Message, failureTitle,
                OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }

    /// <summary>
    /// 重新可見後的第一格畫面之前，替殼層那扇 HwndSource 的根補一次排版。
    /// </summary>
    /// <remarks>
    /// 自動隱藏的工具窗收合時，殼層把滑出面板 HwndSource 的根設成 Collapsed。這段期間內容若還在讓排版失效
    /// （欄位多的 DataGrid 收合當下仍有列虛擬化的延後工作），排版佇列處理到根時會把它移出佇列，但留著髒旗標。
    /// 根變回 Visible 時，WPF 只通知父層（根沒有父層），並呼叫 <c>InvalidateArrange</c>（根早就是髒的，所以沒有作用）。
    /// 只有 HwndSource 收到 <c>WM_SIZE</c> 時才會直接替根量排；滑出不改尺寸，於是整棵樹再也不排版：畫面停在
    /// 舊內容、點擊有收到卻不更新、殼層的標題列也不見。展開別的滑出面板會改到尺寸，所以會恢復。
    ///
    /// 所以在重新可見後的第一個 <see cref="CompositionTarget.Rendering"/>（這一格的排版已跑完、還沒畫）檢查根：
    /// 還無效就是卡住了，照 <c>HwndSource.SetLayoutSize</c> 的作法直接量、排一次，這一格就畫出正確內容。
    /// 只看結果、不看原因：正常情況或 SSMS／WPF 日後修正後，根在那時都已有效，這裡什麼都不做。
    /// 新版 SSMS 下重現步驟不再記到「已補排」那一行，就可以整段刪掉。
    /// </remarks>
    private void OnHostIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!Host.IsVisible || _relayoutPending)
        {
            return;
        }

        _relayoutPending = true;
        CompositionTarget.Rendering += OnFirstFrameAfterShown;
    }

    private void OnFirstFrameAfterShown(object? sender, EventArgs args)
    {
        CompositionTarget.Rendering -= OnFirstFrameAfterShown;
        _relayoutPending = false;
        SqlAssistPlatformGuard.Run("補排工具窗殼層的根", RelayoutStuckRoot);
    }

    private void RelayoutStuckRoot()
    {
        if (!Host.IsVisible || PresentationSource.FromVisual(Host)?.RootVisual is not UIElement root ||
            (root.IsMeasureValid && root.IsArrangeValid))
        {
            return;
        }

        // 沒排過的根沒有可沿用的尺寸，交還殼層自己處理。
        var size = root.RenderSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        root.InvalidateMeasure();
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        SqlAssistDiagnostics.Write("工具窗殼層的根卡在排版佇列外，已補排：" + GetType().Name + " " + size);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _relayoutPending)
        {
            CompositionTarget.Rendering -= OnFirstFrameAfterShown;
            _relayoutPending = false;
        }

        base.Dispose(disposing);
    }
}
