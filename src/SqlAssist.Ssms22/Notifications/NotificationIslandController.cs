using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Notifications;

/// <summary>
/// 決定通知島何時顯示、錨在哪一個視窗上；整個處理程序一份。
/// </summary>
/// <remarks>
/// 「該顯示什麼」在 <see cref="NotificationPresenter"/>，形態在 <see cref="NotificationIslandState"/>，
/// 活動的期限在 <see cref="NotificationLifecycle"/>，視窗本身在 <see cref="NotificationOverlay"/>。
/// 計時器與通知、設定、主題、作用中編輯區的訂閱都只有這一份，套件初始化時就接上：
/// 等第一個編輯區出現才開始的版本，啟動時檢查到的新版本會因為還沒有地方畫而被吃掉。
///
/// 擁有者是作用中編輯區所在的頂層視窗（主視窗或拆出去的文件框架），沒有編輯區時是主視窗。
/// SqlAssist 的對話框不當錨點：右下角是「確定／取消」。
///
/// 早退：沒有東西要顯示時不跑計時器，也不重畫；浮層沒有在畫面上時，主題與編輯區的事件不排程刷新。
/// 狀態只在 UI 執行緒上讀寫；通知與設定的事件可能來自任何執行緒，那裡只排程刷新。
/// </remarks>
internal sealed class NotificationIslandController
{
    public static NotificationIslandController Default { get; } = new();

    private readonly NotificationIslandState _state = new();
    private NotificationOverlay? _overlay;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _timer;
    private ThemeRefreshQueue? _refresh;
    private NotificationIslandContent? _rendered;
    private DateTimeOffset _visibleAt;
    private DateTimeOffset? _hidingAt;
    private bool _attached;
    private bool _retaining;
    private bool _shutdown;

    /// <summary>
    /// 浮層正在畫面上（含收場途中）。
    /// </summary>
    /// <remarks>只在 UI 執行緒上寫入；主題與作用中編輯區的事件用它早退，不為看不見的浮層排程一輪。</remarks>
    private volatile bool _engaged;

    private NotificationIslandController() { }

    private static bool Motion => SqlAssistChrome.MotionEnabled;

    /// <summary>套件初始化時在 UI 執行緒上呼叫一次。</summary>
    public void Start(Dispatcher dispatcher)
    {
        if (dispatcher is null) throw new ArgumentNullException(nameof(dispatcher));
        if (_shutdown || _dispatcher is not null) return;
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        _refresh = new ThemeRefreshQueue(dispatcher, () => SqlAssistPlatformGuard.Probe("更新通知島", Refresh));
        NotificationPresenter.Default.Changed += OnNotifications;
        ActiveSqlEditor.Changed += OnActiveEditor;
        SqlAssistSettingsStore.Changed += OnSettings;
        VsThemeBrushes.Changed += OnTheme;
        // 初始化之前就送出的提醒（例如更新檢查）也要畫出來。
        _refresh.Request();
    }

    /// <summary>套件卸載：停掉計時器與訂閱，關掉浮層。</summary>
    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;
        _engaged = false;
        if (_timer is not null) { _timer.Stop(); _timer.Tick -= OnTick; }
        _refresh?.Dispose();
        if (_dispatcher is not null)
        {
            NotificationPresenter.Default.Changed -= OnNotifications;
            ActiveSqlEditor.Changed -= OnActiveEditor;
            SqlAssistSettingsStore.Changed -= OnSettings;
            VsThemeBrushes.Changed -= OnTheme;
        }

        if (_overlay is { } overlay)
        {
            _overlay = null;
            overlay.Island.StopMotion();
            overlay.Detach();
            overlay.Close();
        }
    }

    /// <summary>「聚焦通知」命令：鍵盤進到島嶼上；沒有東西可以聚焦時回 false，由命令自己說明。</summary>
    public bool Focus() => _overlay is { } overlay && overlay.EnterKeyboard();

    private NotificationOverlay Acquire()
    {
        if (_overlay is { } existing) return existing;
        var overlay = new NotificationOverlay();
        var island = overlay.Island;
        island.MouseEnter += (_, _) => OnPointer(entered: true);
        island.MouseLeave += (_, _) => OnPointer(entered: false);
        island.IsKeyboardFocusWithinChanged += (_, args) => SqlAssistPlatformGuard.Run("切換通知島焦點", () =>
        { _state.FocusChanged((bool)args.NewValue, DateTimeOffset.UtcNow); Schedule(); });
        island.PeekRequested += (_, _) => SqlAssistPlatformGuard.Run("切換通知島暫看", () =>
        { _state.TogglePeek(DateTimeOffset.UtcNow); Schedule(); });
        island.DismissRequested += (_, _) => SqlAssistPlatformGuard.Run("關閉通知", () =>
        {
            // 只隱藏目前這一批活動，不取消工作；提醒各自處理。
            NotificationPresenter.Default.Dismiss(SqlAssistSettingsStore.Current);
            Schedule();
        });
        island.PromptResolved += (id, action) => SqlAssistPlatformGuard.Run("處理通知提醒", () =>
        {
            NotificationActionRouter.Invoke(id, action);
            Schedule();
        });
        island.Vanished += (_, _) => _engaged = false;
        overlay.AnchorStateChanged += (_, _) => SqlAssistPlatformGuard.Probe("通知島擁有者改變", Schedule);
        // 萬一還是跟著擁有者被關掉了，下一輪重建一個，不留一個已經關閉的視窗在手上。
        overlay.Closed += (_, _) => { if (ReferenceEquals(_overlay, overlay)) { _overlay = null; _attached = false; _engaged = false; } };
        _overlay = overlay;
        return overlay;
    }

    private void OnPointer(bool entered) => SqlAssistPlatformGuard.Run("通知島停駐", () =>
    {
        var now = DateTimeOffset.UtcNow;
        if (entered) _state.PointerEntered(now); else _state.PointerExited(now);
        Schedule();
    });

    private void Schedule()
    {
        _refresh?.Request();
        // 停駐展開與移開收回要等時間到；計時器可能因為只剩提醒而停著。
        if (_attached) _timer?.Start();
    }

    private void OnNotifications(object? sender, EventArgs args) =>
        SqlAssistPlatformGuard.Probe("排程通知島", () => _refresh?.Request());

    private void OnActiveEditor(object? sender, EventArgs args)
    {
        // 浮層不在畫面上時，下一次顯示才決定擁有者；換編輯區本身沒有東西要畫。
        if (!_engaged) return;
        SqlAssistPlatformGuard.Probe("切換通知島擁有者", () => _refresh?.Request());
    }

    private void OnSettings(object? sender, EventArgs args) =>
        SqlAssistPlatformGuard.Probe("排程通知島", () => _refresh?.Request());

    private void OnTheme(object? sender, EventArgs args)
    {
        if (!_engaged) return;
        SqlAssistPlatformGuard.Probe("更新通知島配色", () => _refresh?.Request());
    }

    private void OnTick(object? sender, EventArgs args) => SqlAssistPlatformGuard.Probe("更新通知島", Refresh);

    private void Refresh()
    {
        if (_shutdown || _timer is null) return;
        var settings = SqlAssistSettingsStore.Current;
        var now = DateTimeOffset.UtcNow;
        if (!settings.Enabled || !settings.NotificationEnabled)
        {
            _timer.Stop();
            Retire(now);
            return;
        }

        var anchor = ResolveAnchor();
        if (anchor is null || anchor.WindowState == WindowState.Minimized || !anchor.IsVisible)
        {
            // 暫時看不到不算結束：不問通知來源（一問就會跑到期清理），還原時重新長出來。
            _timer.Stop();
            Suspend();
            return;
        }

        var overlay = Acquire();
        _retaining = _attached && overlay.Retaining;
        var presenter = NotificationPresenter.Default;
        var content = presenter.Island(settings, _retaining);
        var count = content.Activities.Count + content.Prompts.Count;
        // 延遲顯示只給活動；提醒是要使用者決定的事，不等。
        var delayed = content.Prompts.Count == 0 &&
            presenter.WithinDelay(TimeSpan.FromMilliseconds(settings.NotificationDelay), now);
        switch (NotificationLifecycle.Next(count, _attached, now, _visibleAt, _hidingAt, Motion, delayed))
        {
            case NotificationStep.Idle:
                _timer.Stop();
                return;
            case NotificationStep.Hold:
                _timer.Start();
                return;
            case NotificationStep.FadeOut:
                _timer.Start();
                _hidingAt = now;
                Render(overlay, NotificationIslandContent.Empty, now, force: true);
                return;
            case NotificationStep.Retire:
                _timer.Stop();
                Retire(now);
                return;
        }

        if (overlay.Attach(anchor)) _attached = false;
        if (!_attached) { _visibleAt = now; _rendered = null; }
        _attached = true;
        _hidingAt = null;
        overlay.Island.SetOptions(settings.NotificationGlass, SystemParameters.HighContrast);
        Render(overlay, content, now, force: false);
        overlay.Present();
        _engaged = true;
        // 只剩提醒、又沒有等著發生的停駐轉換時，沒有東西會自己到期。
        if (content.Activities.Count == 0 && _state.Deadline is null) _timer.Stop();
        else _timer.Start();
    }

    /// <summary>內容與形態都沒變時不重畫：計時器每 100 ms 一輪，每一輪重量一次版面只是在燒 CPU。</summary>
    private void Render(NotificationOverlay overlay, NotificationIslandContent content, DateTimeOffset now, bool force)
    {
        var changed = _state.Update(new NotificationIslandInput(
            content.Activities.Count, content.Running, content.Failed, content.Prompts.Count), now);
        if (!force && !changed && ReferenceEquals(content, _rendered)) return;
        _rendered = content;
        overlay.Island.Update(content, _state, Motion);
    }

    /// <summary>這一批結束：到期、關閉或停用。島嶼自己播收場，播完由浮層隱藏視窗。</summary>
    private void Retire(DateTimeOffset now)
    {
        Release();
        if (_attached && _overlay is { } overlay) Render(overlay, NotificationIslandContent.Empty, now, force: true);
        _attached = false;
        _hidingAt = null;
        _rendered = null;
    }

    /// <summary>擁有者最小化或暫時不存在：立刻隱藏，不播收場；還原時重新長出來。</summary>
    private void Suspend()
    {
        Release();
        if (_overlay is { } overlay)
        {
            overlay.Hide();
            overlay.Island.Reset();
        }

        _attached = false;
        _hidingAt = null;
        _rendered = null;
        _engaged = false;
    }

    private void Release()
    {
        if (!_retaining) return;
        _retaining = false;
        NotificationPresenter.Default.Release(SqlAssistSettingsStore.Current);
    }

    /// <summary>作用中編輯區所在的頂層視窗；沒有編輯區或找不到時是主視窗。</summary>
    private static Window? ResolveAnchor()
    {
        var main = Application.Current?.MainWindow;
        if (ActiveSqlEditor.Current?.VisualElement is not { } editor) return main;
        if (Window.GetWindow(editor) is { } window && window is not NotificationOverlay) return window;
        // 編輯器裝在 HwndHost 裡時 GetWindow 回 null；改從原生控制代碼往上找頂層視窗，再對回 WPF 視窗。
        if (PresentationSource.FromVisual(editor) is not HwndSource source) return main;
        var root = GetAncestor(source.Handle, RootAncestor);
        foreach (Window candidate in Application.Current!.Windows)
            if (candidate is not NotificationOverlay && new WindowInteropHelper(candidate).Handle == root) return candidate;
        return main;
    }

    private const uint RootAncestor = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
