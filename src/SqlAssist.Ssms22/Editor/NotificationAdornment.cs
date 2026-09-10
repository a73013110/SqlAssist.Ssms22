using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Editor;

[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class NotificationAdornmentProvider : IWpfTextViewCreationListener
{
    internal static event Action? Closing;
    internal static void Shutdown() => Closing?.Invoke();
    [Export(typeof(AdornmentLayerDefinition))]
    [Name(NotificationAdornment.LayerName)]
    [Order(After = PredefinedAdornmentLayers.Caret)]
    internal AdornmentLayerDefinition Layer = null!;

    public void TextViewCreated(IWpfTextView textView) =>
        SqlAssistPlatformGuard.Run("建立通知提示", () =>
            textView.Properties.GetOrCreateSingletonProperty(() => new NotificationAdornment(textView)));
}

/// <summary>
/// 把通知卡片掛進編輯器並定位。
/// </summary>
/// <remarks>
/// 「該顯示什麼」在 <see cref="NotificationHost"/>；這裡只管宿主：什麼時候掛上、擺哪裡、
/// 什麼時候收掉。捲動與尺寸變化只走 <see cref="PositionSurface"/>，不進內容刷新——
/// 那一條會進通知來源的鎖、跑到期清理與投影，而捲動不會讓其中任何一項改變。
/// </remarks>
internal sealed class NotificationAdornment : IDisposable
{
    public const string LayerName = "SqlAssist.NotificationCenter";
    private readonly IWpfTextView _view;
    private readonly DispatcherTimer _timer;
    private readonly ThemeRefreshQueue _refresh;
    private NotificationCard? _surface;
    private IAdornmentLayer? _layer;
    private bool _attached;
    private bool _disposed;
    private DateTimeOffset _visibleAt;
    private DateTimeOffset? _hidingAt;
    private bool _retaining;

    /// <summary>
    /// 這個編輯區是不是目前作用中的那一個。
    /// </summary>
    /// <remarks>
    /// 只在 UI 執行緒上寫入。通知來源的事件可能來自任何執行緒，而那裡不能碰編輯器的
    /// 相依屬性，因此非作用中的早退只看這一個旗標；真正的判斷仍在 <see cref="Refresh"/>。
    /// </remarks>
    private volatile bool _active;
    internal NotificationPosition Position { get; set; } = NotificationPosition.TopRight;
    private bool Motion => SqlAssistChrome.NotificationMotionEnabled(SqlAssistSettingsStore.Current.NotificationAnimation,
        SqlAssistSettingsStore.Current.NotificationForceAnimation, SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast);

    public NotificationAdornment(IWpfTextView view)
    {
        _view = view;
        _active = ReferenceEquals(ActiveSqlEditor.Current, view);
        _timer = new DispatcherTimer(DispatcherPriority.Background, view.VisualElement.Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        _refresh = new ThemeRefreshQueue(view.VisualElement.Dispatcher,
            () => SqlAssistPlatformGuard.Probe("更新通知提示", Refresh));
        ActiveSqlEditor.Changed += OnActiveEditor;
        NotificationHost.Default.Changed += OnNotifications;
        NotificationAdornmentProvider.Closing += Dispose;
        SqlAssistSettingsStore.Changed += OnSettings;
        VsThemeBrushes.Changed += OnTheme;
        view.Closed += OnClosed;
        view.VisualElement.IsVisibleChanged += OnVisibility;
        view.ViewportWidthChanged += OnViewport;
        view.ViewportHeightChanged += OnViewport;
        view.ViewportLeftChanged += OnViewport;
        _refresh.Request();
    }

    // 捲動與改變大小只搬動提示；內容留給通知來源與設定的事件。
    private void OnViewport(object? sender, EventArgs args) =>
        SqlAssistPlatformGuard.Probe("定位通知提示", PositionSurface);

    private void OnNotifications(object? sender, EventArgs args)
    {
        // 非作用中的編輯區不為別人的通知重算；換到它時另有 ActiveSqlEditor.Changed。
        if (!_active) return;
        SqlAssistPlatformGuard.Probe("排程通知提示", _refresh.Request);
    }

    private void OnActiveEditor(object? sender, EventArgs args) => SqlAssistPlatformGuard.Probe("切換通知編輯區", () =>
    {
        var active = ReferenceEquals(ActiveSqlEditor.Current, _view);
        var was = _active;
        _active = active;
        // 沒顯示過、現在也不是作用中的編輯區沒有東西要收。
        if (!active && !was && !_attached) return;
        _refresh.Request();
    });

    private void OnVisibility(object sender, DependencyPropertyChangedEventArgs args) => _refresh.Request();
    private void OnSettings(object? sender, EventArgs args)
    {
        if (!_active && !_attached) return;
        _refresh.Request();
    }

    private void OnTheme(object? sender, EventArgs args)
    {
        if (!_attached) return;
        SqlAssistPlatformGuard.Probe("更新通知配色", _refresh.Request);
    }

    private void OnTick(object? sender, EventArgs args) => SqlAssistPlatformGuard.Probe("更新通知提示", Refresh);

    private void Refresh()
    {
        if (_disposed || _view.IsClosed) return;
        var settings = SqlAssistSettingsStore.Current;
        if (!settings.Enabled || !settings.NotificationEnabled || ActiveSqlEditor.Current != _view || !_view.VisualElement.IsVisible)
        { _timer.Stop(); Hide(); return; }
        var now = DateTimeOffset.UtcNow;
        _retaining = _attached && (_surface?.IsMouseOver == true || _surface?.IsKeyboardFocusWithin == true);
        var host = NotificationHost.Default;
        var items = host.Current(settings, _retaining);
        if (items.Count == 0)
        {
            if (!_attached) { _timer.Stop(); return; }
            if (now - _visibleAt < TimeSpan.FromMilliseconds(800)) return;
            if (!Motion) { Hide(); _timer.Stop(); return; }
            if (_hidingAt is null)
            {
                _hidingAt = now;
                _surface?.Transition(false, false, Motion, Position);
            }
            if (now - _hidingAt.Value >= TimeSpan.FromMilliseconds(220)) { Hide(); _timer.Stop(); }
            return;
        }
        _timer.Start();
        if (!_attached && host.WithinDelay(TimeSpan.FromMilliseconds(settings.NotificationDelay), now)) return;
        if (_surface is null)
        {
            _surface = SqlAssistChrome.CreateNotificationCard();
            _surface.SummaryButton.Click += OnToggle;
            _surface.ToggleButton.Click += OnToggle;
            _surface.CloseButton.Click += OnDismiss;
            _surface.MouseEnter += OnPointer;
            _surface.MouseLeave += OnPointer;
            _surface.IsKeyboardFocusWithinChanged += OnInteraction;
            _surface.SizeChanged += OnSurfaceSizeChanged;
            VsThemeBrushes.Apply(_surface);
        }
        _layer ??= _view.GetAdornmentLayer(LayerName);
        _surface.SetOptions(settings.NotificationGlass, SystemParameters.HighContrast);
        _surface.Update(items, host.Expanded, Motion);
        PositionSurface();
        if (!_attached)
        {
            _surface.Opacity = 0;
            _attached = _layer.AddAdornment(AdornmentPositioningBehavior.ViewportRelative, null, this, _surface,
                (_, _) => _attached = false);
            _visibleAt = now;
            _surface.Transition(true, true, Motion, Position);
        }
        else if (_hidingAt.HasValue) _surface.Transition(true, false, Motion, Position);
        _hidingAt = null;
        if (!Motion)
        {
            _surface.ResetTransition();
        }
    }

    private void PositionSurface()
    {
        if (_surface is null || _disposed || _view.IsClosed) return;
        _surface.Constrain(new Size(_view.ViewportWidth, _view.ViewportHeight));
        _surface.Measure(new Size(_surface.MaxWidth, _surface.MaxHeight));
        var point = SqlAssistChrome.NotificationAnchor(new Size(_view.ViewportWidth, _view.ViewportHeight), _surface.DesiredSize, Position);
        Canvas.SetLeft(_surface, _view.ViewportLeft + point.X);
        Canvas.SetTop(_surface, _view.ViewportTop + point.Y);
    }

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs args) =>
        SqlAssistPlatformGuard.Probe("定位通知提示", PositionSurface);
    private void OnPointer(object sender, MouseEventArgs args) => SqlAssistPlatformGuard.Probe("暫停通知倒數", _refresh.Request);
    private void OnInteraction(object sender, DependencyPropertyChangedEventArgs args) => _refresh.Request();
    private void OnDismiss(object sender, RoutedEventArgs args) => SqlAssistPlatformGuard.Run("關閉通知提示", () =>
    {
        // 只隱藏目前批次，不取消 SQL；下一個新工作仍能通知。
        NotificationHost.Default.Dismiss(SqlAssistSettingsStore.Current);
        Hide(); _timer.Stop(); args.Handled = true;
    });

    private void OnToggle(object sender, RoutedEventArgs args) => SqlAssistPlatformGuard.Run("切換通知明細", () =>
    { NotificationHost.Default.Toggle(); Refresh(); args.Handled = true; });

    private void Hide()
    {
        if (_retaining)
        {
            _retaining = false;
            NotificationHost.Default.Release(SqlAssistSettingsStore.Current);
        }
        _surface?.StopMotion();
        if (_attached && _surface is not null) _layer?.RemoveAdornment(_surface);
        _attached = false;
        _hidingAt = null;
    }

    private void OnClosed(object sender, EventArgs args) => SqlAssistPlatformGuard.Run("關閉通知提示", Dispose);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _active = false;
        _timer.Stop(); _timer.Tick -= OnTick;
        _refresh.Dispose();
        ActiveSqlEditor.Changed -= OnActiveEditor;
        NotificationHost.Default.Changed -= OnNotifications;
        NotificationAdornmentProvider.Closing -= Dispose;
        SqlAssistSettingsStore.Changed -= OnSettings;
        VsThemeBrushes.Changed -= OnTheme;
        _view.Closed -= OnClosed;
        _view.VisualElement.IsVisibleChanged -= OnVisibility;
        _view.ViewportWidthChanged -= OnViewport;
        _view.ViewportHeightChanged -= OnViewport;
        _view.ViewportLeftChanged -= OnViewport;
        if (_surface is not null)
        {
            _surface.SummaryButton.Click -= OnToggle;
            _surface.ToggleButton.Click -= OnToggle;
            _surface.CloseButton.Click -= OnDismiss;
            _surface.MouseEnter -= OnPointer;
            _surface.MouseLeave -= OnPointer;
            _surface.IsKeyboardFocusWithinChanged -= OnInteraction;
            _surface.SizeChanged -= OnSurfaceSizeChanged;
        }
        Hide();
    }
}
