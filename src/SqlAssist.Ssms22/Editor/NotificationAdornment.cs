using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
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
    internal static void Shutdown()
    {
        Closing?.Invoke();
        // 每個編輯區都放手了，卡片卻是全域的：不收掉的話動畫會留在一個沒有人看的元素上。
        NotificationSurface.Default.Shutdown();
    }
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
/// 「該顯示什麼」在 <see cref="NotificationHost"/>，卡片本身在 <see cref="NotificationSurface"/>；
/// 這裡只管宿主：什麼時候掛上、擺哪裡、什麼時候收掉。捲動與尺寸變化只走
/// <see cref="PositionSurface"/>，不進內容刷新——那一條會進通知來源的鎖、跑到期清理與投影，
/// 而捲動不會讓其中任何一項改變。
///
/// 卡片不歸這裡持有：每個編輯區各一張的版本，會讓 F12 開新查詢視窗或切分頁時同一份提示
/// 整個重建，於是列的身分、展開狀態與進場動畫全部重來。
/// </remarks>
internal sealed class NotificationAdornment : IDisposable, INotificationSurfaceOwner
{
    public const string LayerName = "SqlAssist.NotificationCenter";

    /// <summary>看得見之後至少留這麼久，免得一閃而過。</summary>
    private static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(800);

    /// <summary>淡出的長度；與 <see cref="NotificationCard.Transition"/> 的收場一致。</summary>
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(220);

    private readonly IWpfTextView _view;
    private readonly DispatcherTimer _timer;
    private readonly ThemeRefreshQueue _refresh;
    private IAdornmentLayer? _layer;
    private bool _disposed;
    private bool _retaining;

    /// <summary>
    /// 這個編輯區是不是目前作用中的那一個。
    /// </summary>
    /// <remarks>
    /// 只在 UI 執行緒上寫入。通知來源的事件可能來自任何執行緒，而那裡不能碰編輯器的
    /// 相依屬性，因此非作用中的早退只看這一個旗標；真正的判斷仍在 <see cref="Refresh"/>。
    /// </remarks>
    private volatile bool _active;
    public NotificationPosition Position { get; set; } = NotificationPosition.TopRight;
    private static NotificationSurface Surface => NotificationSurface.Default;
    private bool Owns => Surface.IsOwnedBy(this);
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
    private void OnViewport(object? sender, EventArgs args) => Reposition();

    /// <summary>卡片掛在別的編輯區上時不去動它的位置。</summary>
    public void Reposition() => SqlAssistPlatformGuard.Probe("定位通知提示", () =>
    { if (Owns) PositionSurface(); });

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
        if (!active && !was && !Owns) return;
        _refresh.Request();
    });

    private void OnVisibility(object sender, DependencyPropertyChangedEventArgs args) => _refresh.Request();
    private void OnSettings(object? sender, EventArgs args)
    {
        if (!_active && !Owns) return;
        _refresh.Request();
    }

    private void OnTheme(object? sender, EventArgs args)
    {
        if (!Owns) return;
        SqlAssistPlatformGuard.Probe("更新通知配色", _refresh.Request);
    }

    private void OnTick(object? sender, EventArgs args) => SqlAssistPlatformGuard.Probe("更新通知提示", Refresh);

    private void Refresh()
    {
        if (_disposed || _view.IsClosed) return;
        var settings = SqlAssistSettingsStore.Current;
        var now = DateTimeOffset.UtcNow;
        var enabled = settings.Enabled && settings.NotificationEnabled;
        if (!enabled || ActiveSqlEditor.Current != _view || !_view.VisualElement.IsVisible)
        {
            // 只是換到別的編輯區時不算結束：卡片留給接手的那一個，不停動畫也不重播入場。
            _timer.Stop(); Hide(now, retire: !enabled); return;
        }
        _retaining = Owns && Surface.Retaining;
        var host = NotificationHost.Default;
        var items = host.Current(settings, _retaining);
        if (items.Count == 0)
        {
            if (!Owns) { _timer.Stop(); return; }
            if (now - Surface.VisibleAt < MinimumVisible) return;
            if (!Motion) { Hide(now, retire: true); _timer.Stop(); return; }
            if (Surface.HidingAt is null)
            {
                Surface.HidingAt = now;
                Surface.Card?.Transition(false, false, Motion, Position);
            }
            if (now - Surface.HidingAt.Value >= FadeOut) { Hide(now, retire: true); _timer.Stop(); }
            return;
        }
        _timer.Start();
        // 卡片已經在畫面上的話延遲早就付過了；這裡擋的是這一批第一次出現。
        if (!Surface.Attached && host.WithinDelay(TimeSpan.FromMilliseconds(settings.NotificationDelay), now)) return;
        var card = Surface.Acquire();
        _layer ??= _view.GetAdornmentLayer(LayerName);
        card.SetOptions(settings.NotificationGlass, SystemParameters.HighContrast);
        card.Update(items, host.Expanded, Motion);
        PositionSurface();
        var hiding = Surface.HidingAt.HasValue;
        if (Surface.Attach(this, _layer, now)) card.Transition(true, true, Motion, Position);
        else if (hiding) card.Transition(true, false, Motion, Position);
        Surface.HidingAt = null;
        if (!Motion) card.ResetTransition();
    }

    private void PositionSurface()
    {
        if (_disposed || _view.IsClosed || Surface.Card is not { } card) return;
        card.Constrain(new Size(_view.ViewportWidth, _view.ViewportHeight));
        card.Measure(new Size(card.MaxWidth, card.MaxHeight));
        var point = SqlAssistChrome.NotificationAnchor(new Size(_view.ViewportWidth, _view.ViewportHeight), card.DesiredSize, Position);
        Canvas.SetLeft(card, _view.ViewportLeft + point.X);
        Canvas.SetTop(card, _view.ViewportTop + point.Y);
    }

    public void Invalidate() => SqlAssistPlatformGuard.Probe("暫停通知倒數", _refresh.Request);

    public void DismissBatch() => SqlAssistPlatformGuard.Run("關閉通知提示", () =>
    {
        // 只隱藏目前批次，不取消 SQL；下一個新工作仍能通知。
        NotificationHost.Default.Dismiss(SqlAssistSettingsStore.Current);
        Hide(DateTimeOffset.UtcNow, retire: true); _timer.Stop();
    });

    public void ToggleDetails() => SqlAssistPlatformGuard.Run("切換通知明細", () =>
    { NotificationHost.Default.Toggle(); Refresh(); });

    private void Hide(DateTimeOffset now, bool retire)
    {
        if (_retaining)
        {
            _retaining = false;
            NotificationHost.Default.Release(SqlAssistSettingsStore.Current);
        }
        Surface.Detach(this, now, retire);
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
        // 關掉這一個查詢視窗不代表這一批結束：還有別的 SQL 編輯區時由它接手。
        Hide(DateTimeOffset.UtcNow, retire: false);
        _layer = null;
    }
}
