using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Notifications;

/// <summary>
/// 決定卡片何時顯示、掛在哪一個宿主上；整個處理程序一份。
/// </summary>
/// <remarks>
/// 「該顯示什麼」在 <see cref="NotificationPresenter"/>，卡片本身在 <see cref="NotificationSurface"/>，
/// 宿主只回報自己的狀態與座標。計時器、通知、設定、主題與作用中編輯區的訂閱都只有這一份：
/// 每個編輯區各有一份的版本，N 個編輯區就是 N 個計時器與 N 份全域訂閱，而且每一份都得
/// 自己維護「我是不是作用中的那一個」的早退旗標。
///
/// 捲動與尺寸變化只走 <see cref="OnViewport"/>，不進內容刷新——那一條會進通知來源的鎖、
/// 跑到期清理與投影，而捲動不會讓其中任何一項改變。
///
/// 狀態只在 UI 執行緒上讀寫；通知與設定的事件可能來自任何執行緒，那裡只讀
/// <see cref="_engaged"/> 並排程刷新。
/// </remarks>
internal sealed class NotificationSurfaceController
{
    public static NotificationSurfaceController Default { get; } = new();

    private readonly List<INotificationSurfaceHost> _hosts = new();
    private readonly NotificationSurface _surface = new();
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _timer;
    private ThemeRefreshQueue? _refresh;
    private bool _retaining;
    private bool _shutdown;

    /// <summary>
    /// 有宿主接得到卡片，或卡片還掛著。
    /// </summary>
    /// <remarks>
    /// 只在 UI 執行緒上寫入。沒有任何宿主可掛時，通知、設定與主題的事件在處理常式就早退，
    /// 不排程一輪才發現沒地方畫。宿主變得可掛時有它自己的狀態事件或作用中編輯區的事件。
    /// </remarks>
    private volatile bool _engaged;

    private NotificationSurfaceController()
    {
        _surface.DetailsToggled += OnDetailsToggled;
        _surface.DismissRequested += OnDismissRequested;
        _surface.RetentionChanged += OnRetentionChanged;
    }

    private static bool Motion => SqlAssistChrome.MotionEnabled;

    /// <summary>宿主可以接卡片了；關閉時要呼叫 <see cref="Unregister"/>。</summary>
    public void Register(INotificationSurfaceHost host)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        if (_shutdown || _hosts.Contains(host)) return;
        Start(host.Dispatcher);
        // 卡片是 DispatcherObject，只掛得進建立它的那條執行緒上的圖層。
        if (host.Dispatcher != _dispatcher) return;
        _hosts.Add(host);
        host.StateChanged += OnHostState;
        host.ViewportChanged += OnViewport;
        _refresh!.Request();
    }

    /// <summary>宿主關了；卡片掛在它身上的話交給下一個宿主。</summary>
    public void Unregister(INotificationSurfaceHost host)
    {
        if (host is null || !_hosts.Remove(host)) return;
        host.StateChanged -= OnHostState;
        host.ViewportChanged -= OnViewport;
        // 關掉這一個宿主不代表這一批結束：還有別的宿主時由它接手，不重播入場。
        if (_surface.IsOwnedBy(host)) Hide(DateTimeOffset.UtcNow, retire: false);
        _refresh?.Request();
    }

    /// <summary>套件卸載：放掉每一個宿主、停掉計時器與訂閱，並收掉卡片。</summary>
    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;
        _engaged = false;
        foreach (var host in _hosts.ToArray()) host.Dispose();
        _hosts.Clear();
        if (_timer is not null) { _timer.Stop(); _timer.Tick -= OnTick; }
        _refresh?.Dispose();
        if (_dispatcher is not null)
        {
            NotificationPresenter.Default.Changed -= OnNotifications;
            ActiveSqlEditor.Changed -= OnActiveEditor;
            SqlAssistSettingsStore.Changed -= OnSettings;
            VsThemeBrushes.Changed -= OnTheme;
        }

        // 宿主都放手了，卡片卻是全域的：不收掉的話動畫會留在一個沒有人看的元素上。
        _surface.Shutdown();
    }

    private void Start(Dispatcher dispatcher)
    {
        if (_dispatcher is not null) return;
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        _refresh = new ThemeRefreshQueue(dispatcher, () => SqlAssistPlatformGuard.Probe("更新通知提示", Refresh));
        NotificationPresenter.Default.Changed += OnNotifications;
        // 編輯區宿主的「最後用過」由這一份決定；接在這裡才不會每個編輯區各訂閱一次。
        ActiveSqlEditor.Changed += OnActiveEditor;
        SqlAssistSettingsStore.Changed += OnSettings;
        VsThemeBrushes.Changed += OnTheme;
    }

    // 捲動與改變大小只搬動提示；內容留給通知來源與設定的事件。
    private void OnViewport(object? sender, EventArgs args) => SqlAssistPlatformGuard.Probe("定位通知提示", () =>
    {
        // 卡片掛在別的宿主上時不去動它的位置。
        if (sender is INotificationSurfaceHost host && _surface.IsOwnedBy(host)) _surface.Place();
    });

    private void OnHostState(object? sender, EventArgs args) => SqlAssistPlatformGuard.Probe("切換通知宿主", () =>
    {
        // 非作用中、也沒掛著卡片的宿主變了不影響選擇，不排程刷新。
        if (sender is not INotificationSurfaceHost host ||
            (!_surface.IsOwnedBy(host) && host.Activity == NotificationSurfaceHostActivity.Inactive)) return;
        _refresh?.Request();
    });

    private void OnNotifications(object? sender, EventArgs args)
    {
        if (!_engaged) return;
        SqlAssistPlatformGuard.Probe("排程通知提示", () => _refresh?.Request());
    }

    private void OnActiveEditor(object? sender, EventArgs args) =>
        SqlAssistPlatformGuard.Probe("切換通知編輯區", () => _refresh?.Request());

    private void OnSettings(object? sender, EventArgs args)
    {
        if (!_engaged) return;
        SqlAssistPlatformGuard.Probe("排程通知提示", () => _refresh?.Request());
    }

    private void OnTheme(object? sender, EventArgs args)
    {
        if (!_engaged) return;
        SqlAssistPlatformGuard.Probe("更新通知配色", () => _refresh?.Request());
    }

    private void OnTick(object? sender, EventArgs args) => SqlAssistPlatformGuard.Probe("更新通知提示", Refresh);

    private void Refresh()
    {
        if (_shutdown || _timer is null) return;
        var settings = SqlAssistSettingsStore.Current;
        var now = DateTimeOffset.UtcNow;
        var enabled = settings.Enabled && settings.NotificationEnabled;
        var host = NotificationHostPriority.Select(_hosts, _surface.Owner);
        if (!enabled || host is null)
        {
            // 只是暫時沒有宿主不算結束：卡片留給接手的那一個，不停動畫也不重播入場。
            _timer.Stop(); Hide(now, retire: !enabled);
            _engaged = host is not null;
            return;
        }

        _engaged = true;
        _retaining = _surface.Attached && _surface.Retaining;
        var presenter = NotificationPresenter.Default;
        var items = presenter.Current(settings, _retaining);
        var step = NotificationLifecycle.Next(items.Count, _surface.Attached, now, _surface.VisibleAt,
            _surface.HidingAt, Motion, presenter.WithinDelay(TimeSpan.FromMilliseconds(settings.NotificationDelay), now));
        switch (step)
        {
            case NotificationStep.Idle:
                _timer.Stop();
                return;
            case NotificationStep.Hold:
                _timer.Start();
                return;
            case NotificationStep.FadeOut:
                _timer.Start();
                _surface.HidingAt = now;
                _surface.Card?.Transition(false, false, Motion);
                return;
            case NotificationStep.Retire:
                Hide(now, retire: true); _timer.Stop();
                return;
        }

        _timer.Start();
        var card = _surface.Acquire();
        card.SetOptions(settings.NotificationGlass, SystemParameters.HighContrast);
        card.Update(items, presenter.Expanded, Motion);
        var hiding = _surface.HidingAt.HasValue;
        if (_surface.Attach(host, now)) card.Transition(true, true, Motion);
        else if (hiding) card.Transition(true, false, Motion);
        _surface.HidingAt = null;
        if (!Motion) card.ResetTransition();
    }

    private void OnDetailsToggled() => SqlAssistPlatformGuard.Run("切換通知明細", () =>
    { NotificationPresenter.Default.Toggle(); Refresh(); });

    private void OnDismissRequested() => SqlAssistPlatformGuard.Run("關閉通知提示", () =>
    {
        // 只隱藏目前批次，不取消 SQL；下一個新工作仍能通知。
        NotificationPresenter.Default.Dismiss(SqlAssistSettingsStore.Current);
        Hide(DateTimeOffset.UtcNow, retire: true); _timer?.Stop();
    });

    private void OnRetentionChanged() => SqlAssistPlatformGuard.Probe("暫停通知倒數", () => _refresh?.Request());

    private void Hide(DateTimeOffset now, bool retire)
    {
        if (_retaining)
        {
            _retaining = false;
            NotificationPresenter.Default.Release(SqlAssistSettingsStore.Current);
        }

        _surface.Detach(now, retire);
    }
}
