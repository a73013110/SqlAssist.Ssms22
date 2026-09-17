using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Notifications;

/// <summary>
/// 讓通知卡片掛在 SqlAssist 自己的 WPF 視窗上（WPF <see cref="AdornerLayer"/>）。
/// </summary>
/// <remarks>
/// 對話框經 <c>SqlAssistDialogs.Configure</c> 自動註冊，關閉時自動放手；其他內容（工具窗）呼叫
/// <see cref="Register"/>，並在自己釋放時釋放傳回的宿主。內容外層要有 <see cref="AdornerDecorator"/>：
/// <see cref="Window"/> 的樣板本來就有，工具窗要自己包一層，否則會找到殼層主視窗的圖層，
/// 卡片就不受工具窗的邊界裁切。
/// </remarks>
internal sealed class NotificationWindowHost : INotificationSurfaceHost
{
    private readonly FrameworkElement _element;
    private CardAdorner? _adorner;
    private bool _disposed;

    private NotificationWindowHost(FrameworkElement element)
    {
        _element = element;
        element.IsVisibleChanged += OnStateProperty;
        element.IsKeyboardFocusWithinChanged += OnStateProperty;
        element.SizeChanged += OnSize;
        if (element is Window window)
        {
            window.Activated += OnState;
            window.Deactivated += OnState;
            window.Closed += OnClosed;
            // 還沒顯示的視窗不進宿主清單：建構後沒有顯示就不會有 Closed，會一直握著它。
            if (PresentationSource.FromVisual(window) is null) { window.SourceInitialized += OnSourceInitialized; return; }
        }

        NotificationSurfaceController.Default.Register(this);
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        ((Window)_element).SourceInitialized -= OnSourceInitialized;
        if (!_disposed) NotificationSurfaceController.Default.Register(this);
    }

    /// <summary>讓這個視窗或內容可以接通知卡片。</summary>
    /// <returns>視窗關閉時自動放手；其他內容要在自己釋放時釋放它。</returns>
    public static IDisposable Register(FrameworkElement element)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        return new NotificationWindowHost(element);
    }

    public event EventHandler? ViewportChanged;
    public event EventHandler? StateChanged;

    public NotificationSurfaceHostKind Kind => NotificationSurfaceHostKind.Window;
    public Dispatcher Dispatcher => _element.Dispatcher;

    public bool IsVisible => !_disposed && _element.IsVisible && Target is { } target && AdornerLayer.GetAdornerLayer(target) is not null;

    public NotificationSurfaceHostActivity Activity =>
        !_disposed && (_element is Window window ? window.IsActive : _element.IsKeyboardFocusWithin)
            ? NotificationSurfaceHostActivity.Focused
            : NotificationSurfaceHostActivity.Inactive;

    public Rect Viewport => Target is { } target ? new Rect(target.RenderSize) : Rect.Empty;

    /// <summary>卡片貼著的元素：視窗是它的內容，其他元素是自己。</summary>
    /// <remarks>視窗本身在 <see cref="AdornerDecorator"/> 之上，對它找不到圖層。</remarks>
    private UIElement? Target => _element is Window window ? window.Content as UIElement : _element;

    public bool Mount(NotificationCard card, Action removed)
    {
        if (_disposed || Target is not { } target || AdornerLayer.GetAdornerLayer(target) is not { } layer) return false;
        _adorner = new CardAdorner(target, card);
        layer.Add(_adorner);
        return true;
    }

    public void Unmount(NotificationCard card)
    {
        if (_adorner is not { } adorner) return;
        _adorner = null;
        AdornerLayer.GetAdornerLayer(adorner.AdornedElement)?.Remove(adorner);
        adorner.Release();
    }

    // 改變大小只回報座標變了；控制器只在卡片掛在這裡時重新定位。
    private void OnSize(object sender, SizeChangedEventArgs args) => ViewportChanged?.Invoke(this, EventArgs.Empty);

    private void OnState(object? sender, EventArgs args) => StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnStateProperty(object sender, DependencyPropertyChangedEventArgs args) => StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnClosed(object? sender, EventArgs args) => SqlAssistPlatformGuard.Run("關閉通知視窗宿主", Dispose);

    public void Dispose()
    {
        if (_disposed) return;
        // 先放手再標記：控制器交接時還要從這裡拔下卡片。
        NotificationSurfaceController.Default.Unregister(this);
        _disposed = true;
        _element.IsVisibleChanged -= OnStateProperty;
        _element.IsKeyboardFocusWithinChanged -= OnStateProperty;
        _element.SizeChanged -= OnSize;
        if (_element is Window window)
        {
            window.Activated -= OnState;
            window.Deactivated -= OnState;
            window.Closed -= OnClosed;
            window.SourceInitialized -= OnSourceInitialized;
        }
    }

    /// <summary>
    /// 把卡片放在一張與內容同大的 <see cref="Canvas"/> 上。
    /// </summary>
    /// <remarks>
    /// 定位沿用編輯器那一條的 <see cref="Canvas.LeftProperty"/>／<see cref="Canvas.TopProperty"/>，
    /// 卡片與控制器不必分辨宿主。Canvas 沒有背景，只有卡片本身接收指標。
    /// </remarks>
    private sealed class CardAdorner : Adorner
    {
        private readonly Canvas _canvas = new();

        public CardAdorner(UIElement adorned, NotificationCard card) : base(adorned)
        {
            IsClipEnabled = true;
            _canvas.Children.Add(card);
            AddVisualChild(_canvas);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) =>
            index == 0 ? _canvas : throw new ArgumentOutOfRangeException(nameof(index));

        protected override Size MeasureOverride(Size constraint)
        {
            _canvas.Measure(AdornedElement.RenderSize);
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _canvas.Arrange(new Rect(AdornedElement.RenderSize));
            return finalSize;
        }

        /// <summary>卡片要搬到下一個宿主，先離開這張 Canvas。</summary>
        public void Release()
        {
            _canvas.Children.Clear();
            RemoveVisualChild(_canvas);
        }
    }
}
