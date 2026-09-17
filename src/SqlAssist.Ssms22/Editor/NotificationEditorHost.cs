using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Ssms22.Notifications;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Editor;

[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class NotificationEditorHostProvider : IWpfTextViewCreationListener
{
    [Export(typeof(AdornmentLayerDefinition))]
    [Name(NotificationEditorHost.LayerName)]
    [Order(After = PredefinedAdornmentLayers.Caret)]
    internal AdornmentLayerDefinition Layer = null!;

    public void TextViewCreated(IWpfTextView textView) =>
        SqlAssistPlatformGuard.Run("建立通知提示", () =>
            textView.Properties.GetOrCreateSingletonProperty(() => new NotificationEditorHost(textView)));
}

/// <summary>
/// 讓通知卡片掛在 SQL 編輯區的 adornment 層上。
/// </summary>
/// <remarks>
/// 只回報可見度、焦點與 viewport；何時顯示與計時器在 <see cref="NotificationSurfaceController"/>。
/// 焦點的變化自己回報，「最後用過的編輯區」換人則由控制器訂閱一次 <see cref="ActiveSqlEditor.Changed"/>。
/// </remarks>
internal sealed class NotificationEditorHost : INotificationSurfaceHost
{
    public const string LayerName = "SqlAssist.NotificationCard";

    private readonly IWpfTextView _view;
    private IAdornmentLayer? _layer;
    private bool _disposed;

    public NotificationEditorHost(IWpfTextView view)
    {
        _view = view;
        view.Closed += OnClosed;
        view.GotAggregateFocus += OnState;
        view.LostAggregateFocus += OnState;
        view.VisualElement.IsVisibleChanged += OnVisibility;
        view.ViewportWidthChanged += OnViewport;
        view.ViewportHeightChanged += OnViewport;
        view.ViewportLeftChanged += OnViewport;
        NotificationSurfaceController.Default.Register(this);
    }

    public event EventHandler? ViewportChanged;
    public event EventHandler? StateChanged;

    public NotificationSurfaceHostKind Kind => NotificationSurfaceHostKind.Editor;
    public Dispatcher Dispatcher => _view.VisualElement.Dispatcher;
    public bool IsVisible => !_disposed && !_view.IsClosed && _view.VisualElement.IsVisible;

    public NotificationSurfaceHostActivity Activity =>
        _disposed || _view.IsClosed ? NotificationSurfaceHostActivity.Inactive
        : _view.HasAggregateFocus ? NotificationSurfaceHostActivity.Focused
        : ReferenceEquals(ActiveSqlEditor.Current, _view) ? NotificationSurfaceHostActivity.Recent
        : NotificationSurfaceHostActivity.Inactive;

    // adornment 層是 ViewportRelative，但 Canvas 座標仍是文字座標，要加上 viewport 的起點。
    public Rect Viewport => new(_view.ViewportLeft, _view.ViewportTop, _view.ViewportWidth, _view.ViewportHeight);

    public bool Mount(NotificationCard card, Action removed)
    {
        if (_disposed || _view.IsClosed) return false;
        _layer ??= _view.GetAdornmentLayer(LayerName);
        return _layer.AddAdornment(AdornmentPositioningBehavior.ViewportRelative, null, this, card, (_, _) => removed());
    }

    public void Unmount(NotificationCard card) => _layer?.RemoveAdornment(card);

    // 捲動與改變大小只回報座標變了；控制器只在卡片掛在這裡時重新定位。
    private void OnViewport(object? sender, EventArgs args) => ViewportChanged?.Invoke(this, EventArgs.Empty);

    private void OnState(object? sender, EventArgs args) => StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnVisibility(object sender, DependencyPropertyChangedEventArgs args) => StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnClosed(object? sender, EventArgs args) => SqlAssistPlatformGuard.Run("關閉通知提示", Dispose);

    public void Dispose()
    {
        if (_disposed) return;
        // 先放手再標記：控制器交接時還要從這裡拔下卡片。
        NotificationSurfaceController.Default.Unregister(this);
        _disposed = true;
        _view.Closed -= OnClosed;
        _view.GotAggregateFocus -= OnState;
        _view.LostAggregateFocus -= OnState;
        _view.VisualElement.IsVisibleChanged -= OnVisibility;
        _view.ViewportWidthChanged -= OnViewport;
        _view.ViewportHeightChanged -= OnViewport;
        _view.ViewportLeftChanged -= OnViewport;
        _layer = null;
    }
}
