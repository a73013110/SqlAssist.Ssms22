using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using SqlAssist.Core.Preview;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 可確定定位的空間保留代理人。
/// </summary>
/// <remarks>
/// 平台內建 PopupAgent 會依 Windows 的左右手功能表設定改用 PlacementMode.Left，
/// 畫面因此可能與它回報給 reservation stack 的矩形相反。這裡仍加入同一套
/// ISpaceReservationManager 以保留聚合焦點與生命週期，但用 Relative 明確套用座標。
/// </remarks>
internal sealed class SqlPreviewPopupAgent : ISpaceReservationAgent, IDisposable
{
    private const double LayoutGap = 4;
    private const double BoundsPadding = 4;

    private readonly IWpfTextView _view;
    private readonly ISpaceReservationManager _manager;
    private readonly SqlStructurePreviewControl _control;
    private readonly ContentControl _container;
    private readonly ExactPopup _popup;

    private ITrackingSpan _anchor;
    private SqlPreviewPlacement _placement;
    private double? _preferredWidth;
    private double _preferredHeight;
    private IReadOnlyList<PreviewRectangle> _obstacles = Array.Empty<PreviewRectangle>();
    private PreviewRectangle _availableBounds;
    private PreviewRectangle _bounds;
    private PreviewRectangle _resizeStartBounds;
    private PreviewRectangle _resizeBounds;
    private PreviewResizeCorner _resizeCorner;
    private PreviewPlacementSide _side;
    private bool _eventsAttached;
    private bool _focusCheckQueued;
    private bool _hasLayout;
    private bool _isResizing;
    private bool _resizeUpdateQueued;
    private bool _usedFallback;
    private bool _disposed;
    private double _pendingHorizontalChange;
    private double _pendingVerticalChange;
    private Window? _hostWindow;

    /// <summary>一輪定位只查一次的 DPI 轉換；<see cref="RefreshDeviceTransforms"/> 說明為什麼要快取。</summary>
    private Matrix _toDevice = Matrix.Identity;

    private Matrix _fromDevice = Matrix.Identity;

    /// <summary>算出 <see cref="_documentColumnBottom"/> 時的編輯器矩形；沒變就沿用答案。</summary>
    private PreviewRectangle _documentColumnEditor;

    private double _documentColumnBottom;

    private bool _hasDocumentColumn;

    /// <summary>已經回報過內容掛在別的承載視窗上；這件事會每一次重排都成立一次。</summary>
    private bool _reportedDetachedContent;

    public SqlPreviewPopupAgent(
        IWpfTextView view,
        ISpaceReservationManager manager,
        ITrackingSpan anchor,
        SqlStructurePreviewControl control)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        _control = control ?? throw new ArgumentNullException(nameof(control));

        _container = new ContentControl
        {
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        _popup = new ExactPopup
        {
            AllowsTransparency = true,
            PlacementTarget = view.VisualElement,
            Placement = PlacementMode.Relative,
            StaysOpen = true,
            Child = _container
        };
    }

    public bool IsMouseOver => _popup.IsOpen && (_control.IsMouseOver || _control.HasOpenContextMenu);

    public bool HasFocus =>
        _popup.IsOpen && (_popup.IsKeyboardFocusWithin || _control.HasOpenContextMenu);

    public double CurrentWidth => ToLogicalSize(_bounds.Width, _bounds.Height).Width;

    public double CurrentHeight => ToLogicalSize(_bounds.Width, _bounds.Height).Height;

    /// <summary>
    /// 目前這一軸的尺寸是版面壓縮出來的，不是使用者的偏好。
    /// </summary>
    /// <remarks>
    /// 拖曳結束要寫回偏好尺寸時必須先問這個：壓縮值寫回去等於使用者換一次視窗大小，
    /// 記住的尺寸就被永久縮小一次，而且他從來沒有拖過那一軸。
    /// </remarks>
    public bool WidthConstrained { get; private set; }

    public bool HeightConstrained { get; private set; }

    public event EventHandler? LostFocus;

    public event EventHandler? GotFocus;

    public void Update(
        ITrackingSpan anchor,
        SqlPreviewPlacement placement,
        double? preferredWidth,
        double preferredHeight)
    {
        _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        if (_placement != placement)
        {
            _hasLayout = false;
        }

        _placement = placement;
        _preferredWidth = preferredWidth;
        _preferredHeight = preferredHeight;
    }

    /// <summary>要求整個 reservation stack 以最新的建議清單與編輯器幾何重算。</summary>
    public void RequestReposition()
    {
        if (_disposed || _view.IsClosed)
        {
            return;
        }

        SqlAssistPlatformGuard.Run(
            "更新結構預覽位置",
            () => _view.QueueSpaceReservationStackRefresh());
    }

    public Geometry? PositionAndDisplay(Geometry reservedSpace) =>
        SqlAssistPlatformGuard.Run<Geometry?>(
            "定位結構預覽",
            () => PositionAndDisplayCore(reservedSpace),
            fallback: null);

    private Geometry? PositionAndDisplayCore(Geometry reservedSpace)
    {
        if (_disposed || _view.IsClosed)
        {
            return null;
        }

        if (!_view.VisualElement.IsLoaded || _view.TextViewLines is null)
        {
            // 版面尚未建立不是永久失敗；保留 Agent，下一輪 Layout 再試。
            return Geometry.Empty;
        }

        if (TryGetAnchorBounds() is not { } anchorBounds)
        {
            return null;
        }

        RefreshDeviceTransforms();

        if (!_isResizing)
        {
            _obstacles = GetObstacleBounds(reservedSpace);
            _availableBounds = GetAvailableBounds(anchorBounds);
            var desiredSize = ToDeviceSize(
                _preferredWidth ?? SqlAssistLimits.DefaultPreviewWidth,
                _preferredHeight);
            var minimumSize = ToDeviceSize(
                SqlAssistLimits.MinimumPreviewWidth,
                SqlAssistLimits.MinimumPreviewHeight);
            var absoluteMaximumSize = ToDeviceSize(
                SqlAssistLimits.MaximumPreviewWidth,
                SqlAssistLimits.MaximumPreviewHeight);
            var layout = PreviewPlacementEngine.Calculate(
                new PreviewLayoutRequest
                {
                    Placement = _placement,
                    Anchor = anchorBounds,
                    AvailableBounds = _availableBounds,
                    Obstacles = _obstacles,
                    DesiredWidth = desiredSize.Width,
                    DesiredHeight = desiredSize.Height,
                    MinimumWidth = minimumSize.Width,
                    MinimumHeight = minimumSize.Height,
                    MaximumWidth = absoluteMaximumSize.Width,

                    // 不必先跟可用高度取小；引擎本來就會把上下限收進 AvailableBounds。
                    MaximumHeight = absoluteMaximumSize.Height,
                    StretchStackedWidth =
                        _placement == SqlPreviewPlacement.Stacked && !_preferredWidth.HasValue,
                    Gap = ToDeviceSize(LayoutGap, LayoutGap).Width,
                    PreviousSide = _hasLayout ? _side : (PreviewPlacementSide?)null
                });

            if (layout.Bounds.IsEmpty)
            {
                return null;
            }

            _bounds = layout.Bounds;
            _side = layout.Side;
            _hasLayout = true;
            _usedFallback = layout.UsedFallback;
            WidthConstrained = layout.WidthConstrained;
            HeightConstrained = layout.HeightConstrained;
            var resizeOnTop = _side == PreviewPlacementSide.Above ||
                              (_side is PreviewPlacementSide.Left or PreviewPlacementSide.Right &&
                               _bounds.Top - _availableBounds.Top >
                               _availableBounds.Bottom - _bounds.Bottom);
            _control.SetResizeEdge(resizeOnTop);
        }

        Display(_bounds);
        LogPlacement(anchorBounds);
        return CreateReservation(anchorBounds, _bounds);
    }

    public void Hide()
    {
        _control.CloseTransientPopups();
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
        }

        _container.Content = null;
        DetachEvents();
    }

    public void BeginResize(PreviewResizeCorner corner)
    {
        if (_bounds.IsEmpty)
        {
            return;
        }

        // 拖曳期間不再重新定位，因此 DPI 也在這裡凍結一次，與控制項那一端同步。
        RefreshDeviceTransforms();
        _resizeCorner = corner;
        _resizeStartBounds = _bounds;
        _resizeBounds = GetResizeBounds(_bounds, _availableBounds, _obstacles);
        _pendingHorizontalChange = 0;
        _pendingVerticalChange = 0;
        _isResizing = true;
    }

    /// <summary>位移量一律是相對按下瞬間的總量，不從上一幀累加。</summary>
    public void Resize(double horizontalChange, double verticalChange)
    {
        if (!_isResizing)
        {
            return;
        }

        _pendingHorizontalChange = horizontalChange;
        _pendingVerticalChange = verticalChange;
        if (_resizeUpdateQueued)
        {
            return;
        }

        _resizeUpdateQueued = true;
        _view.VisualElement.Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() => SqlAssistPlatformGuard.Run(
                "更新結構預覽拖曳",
                () =>
                {
                    _resizeUpdateQueued = false;
                    if (_isResizing)
                    {
                        ApplyPendingResize();
                    }
                })));
    }

    private void ApplyPendingResize()
    {
        var minimumSize = ToDeviceSize(
            SqlAssistLimits.MinimumPreviewWidth,
            SqlAssistLimits.MinimumPreviewHeight);
        var maximumSize = ToDeviceSize(
            SqlAssistLimits.MaximumPreviewWidth,
            SqlAssistLimits.MaximumPreviewHeight);
        _bounds = PreviewResizeEngine.Resize(
            _resizeStartBounds,
            _resizeCorner,
            _pendingHorizontalChange,
            _pendingVerticalChange,
            _resizeBounds,
            minimumSize.Width,
            minimumSize.Height,
            maximumSize.Width,
            maximumSize.Height);

        // 使用者拖出來的尺寸就是他的偏好，不再是版面壓縮的結果。
        WidthConstrained = false;
        HeightConstrained = false;
        Display(_bounds);

        // 自訂 agent 在拖曳中維持同一個 Rect；重排只更新其他 agent 看見的保留區。
        RequestReposition();
    }

    public void CompleteResize(bool canceled)
    {
        if (!_isResizing)
        {
            return;
        }

        if (canceled)
        {
            _bounds = _resizeStartBounds;
            Display(_bounds);
        }
        else
        {
            // DragCompleted 可能早於最後一個 Render callback；保存前先同步套用最後總位移。
            ApplyPendingResize();
        }

        _isResizing = false;
        RequestReposition();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isResizing = false;
        Hide();
        _popup.Child = null;
    }

    private void Display(PreviewRectangle bounds)
    {
        var logicalSize = ToLogicalSize(bounds.Width, bounds.Height);
        var relativeLocation = _view.VisualElement.PointFromScreen(
            new Point(bounds.Left, bounds.Top));
        _control.SetEffectiveSize(logicalSize.Width, logicalSize.Height);
        _popup.HorizontalOffset = relativeLocation.X;
        _popup.VerticalOffset = relativeLocation.Y;

        if (_container.Content is null && VisualTreeHelper.GetParent(_control) is null)
        {
            _container.Content = _control;
        }

        if (ReferenceEquals(_container.Content, _control))
        {
            if (!_popup.IsOpen)
            {
                AttachEvents();
                _popup.IsOpen = true;
            }

            return;
        }

        // 控制項還掛在上一個 Agent 的容器上。這條路徑不丟例外也不顯示任何東西，
        // 症狀是「按了向右鍵沒反應」；沒有這一行的話紀錄檔會是一片空白。
        // 只記一次：這個狀態會在接下來每一次重排都再成立一次，記成流水帳等於
        // 把真正的錯誤沖掉。
        if (_reportedDetachedContent)
        {
            return;
        }

        _reportedDetachedContent = true;
        SqlAssistDiagnostics.WriteAlways(
            "結構預覽無法顯示：內容仍掛在另一個承載視窗上。",
            _view);
    }

    private PreviewRectangle? TryGetAnchorBounds()
    {
        var span = _anchor.GetSpan(_view.TextSnapshot);
        Rect? textBounds = null;

        if (span.Length > 0)
        {
            var left = double.MaxValue;
            var top = double.MaxValue;
            var right = double.MinValue;
            var bottom = double.MinValue;

            foreach (var bound in _view.TextViewLines.GetNormalizedTextBounds(span))
            {
                left = Math.Min(left, bound.Left);
                top = Math.Min(top, bound.TextTop);
                right = Math.Max(right, bound.Right);
                bottom = Math.Max(bottom, bound.TextBottom);
            }

            var startLine = _view.TextViewLines.GetTextViewLineContainingBufferPosition(span.Start);
            if (startLine is not null)
            {
                var start = startLine.GetExtendedCharacterBounds(span.Start);
                if (start.Left < right &&
                    start.Left >= _view.ViewportLeft &&
                    start.Left < _view.ViewportRight)
                {
                    left = start.Left;
                }
            }

            if (left <= right)
            {
                textBounds = new Rect(left, top, right - left, bottom - top);
            }
        }
        else if (_view.TextViewLines.GetTextViewLineContainingBufferPosition(span.Start) is { } line)
        {
            var bound = line.GetExtendedCharacterBounds(span.Start);
            textBounds = new Rect(bound.Left, bound.TextTop, Math.Max(1, bound.Width), bound.TextHeight);
        }

        if (textBounds is not { } value)
        {
            return null;
        }

        value.Intersect(new Rect(
            _view.ViewportLeft,
            _view.ViewportTop,
            _view.ViewportWidth,
            _view.ViewportHeight));
        if (value.IsEmpty)
        {
            return null;
        }

        var visualTopLeft = new Point(
            value.Left - _view.ViewportLeft,
            value.Top - _view.ViewportTop);
        var visualBottomRight = new Point(
            value.Right - _view.ViewportLeft,
            value.Bottom - _view.ViewportTop);
        var screenTopLeft = _view.VisualElement.PointToScreen(visualTopLeft);
        var screenBottomRight = _view.VisualElement.PointToScreen(visualBottomRight);
        return new PreviewRectangle(
            Math.Min(screenTopLeft.X, screenBottomRight.X),
            Math.Min(screenTopLeft.Y, screenBottomRight.Y),
            Math.Max(1, Math.Abs(screenBottomRight.X - screenTopLeft.X)),
            Math.Max(1, Math.Abs(screenBottomRight.Y - screenTopLeft.Y)));
    }

    /// <summary>
    /// 水平範圍採文字編輯器；垂直範圍延伸到同一文件／主視窗底部，因此結果窗格
    /// 只會被預覽覆蓋，不會再把預覽偏好高度壓成文字 Viewport 的高度。
    /// </summary>
    private PreviewRectangle GetAvailableBounds(PreviewRectangle anchor)
    {
        var visual = _view.VisualElement;
        var viewTopLeft = visual.PointToScreen(new Point(0, 0));
        var viewBottomRight = visual.PointToScreen(
            new Point(Math.Max(1, visual.ActualWidth), Math.Max(1, visual.ActualHeight)));
        var left = Math.Min(viewTopLeft.X, viewBottomRight.X);
        var top = Math.Min(viewTopLeft.Y, viewBottomRight.Y);
        var right = Math.Max(viewTopLeft.X, viewBottomRight.X);
        var bottom = Math.Max(viewTopLeft.Y, viewBottomRight.Y);
        var devicePadding = ToDeviceSize(BoundsPadding, BoundsPadding);
        var editor = new PreviewRectangle(left, top, right - left, bottom - top);
        bottom = ResolveDocumentColumnBottom(visual, editor);

        if (NativeScreen.TryGetWorkArea(new Point(anchor.Left, anchor.Top)) is { } workArea)
        {
            left = Math.Max(left, workArea.Left + devicePadding.Width);
            top = Math.Max(top, workArea.Top + devicePadding.Height);
            right = Math.Min(right, workArea.Right - devicePadding.Width);
            bottom = Math.Min(bottom, workArea.Bottom - devicePadding.Height);
        }

        return new PreviewRectangle(
            left,
            top,
            Math.Max(1, right - left),
            Math.Max(1, bottom - top));
    }

    /// <summary>
    /// 查詢文件欄的底界，也就是「預覽可以蓋到哪裡」——含同一份文件的結果窗格。
    /// </summary>
    /// <remarks>
    /// 這件事要爬一次 WPF 祖先樹，跨不過 HWND 邊界時還要再走一段 Win32 迴圈，
    /// 而答案只有在編輯器本身換大小或重新分割時才會變。定位則是每一次捲動、
    /// 每一個按鍵都要跑一輪，所以用編輯器矩形當鍵快取：矩形沒變就沿用上一次的答案。
    /// </remarks>
    private double ResolveDocumentColumnBottom(FrameworkElement visual, PreviewRectangle editor)
    {
        if (_hasDocumentColumn && _documentColumnEditor == editor)
        {
            return _documentColumnBottom;
        }

        var left = editor.Left;
        var top = editor.Top;
        var right = editor.Right;
        var editorBottom = editor.Bottom;
        var bottom = editorBottom;
        var ancestorTolerance = ToDeviceSize(48, 48);
        var expandedByWpfParent = false;
        var minimumUsefulExpansion = Math.Max(8, ancestorTolerance.Height / 2);

        for (DependencyObject? current = VisualTreeHelper.GetParent(visual);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            // Window 及其殼層子樹可能涵蓋 Output／狀態列，不能當成查詢文件欄。
            if (current is Window)
            {
                break;
            }

            if (current is not FrameworkElement element ||
                element.ActualWidth <= 0 ||
                element.ActualHeight <= 0)
            {
                continue;
            }

            var ancestorTopLeft = element.PointToScreen(new Point(0, 0));
            var ancestorBottomRight = element.PointToScreen(
                new Point(element.ActualWidth, element.ActualHeight));

            // 只接受完整涵蓋查詢編輯器寬度的祖先，避免把相鄰工具視窗算進可用區。
            var sameDocumentColumn =
                Math.Abs(ancestorTopLeft.X - left) <= ancestorTolerance.Width &&
                Math.Abs(ancestorBottomRight.X - right) <= ancestorTolerance.Width &&
                Math.Abs(ancestorTopLeft.Y - top) <= ancestorTolerance.Height;
            if (sameDocumentColumn &&
                ancestorBottomRight.Y > editorBottom + minimumUsefulExpansion)
            {
                bottom = Math.Max(bottom, ancestorBottomRight.Y);
                expandedByWpfParent = true;

                // 最近一個向下擴張的同欄父容器就是 editor/results splitter；不再爬到 Shell root。
                break;
            }
        }

        var editorBounds = new Rect(left, top, right - left, editorBottom - top);
        if (!expandedByWpfParent &&
            NativeScreen.TryGetDocumentColumnBottom(
                visual,
                editorBounds,
                ancestorTolerance.Width) is { } nativeBottom)
        {
            bottom = Math.Max(bottom, nativeBottom);
        }

        _documentColumnEditor = editor;
        _documentColumnBottom = bottom;
        _hasDocumentColumn = true;
        return bottom;
    }

    private IReadOnlyList<PreviewRectangle> GetObstacleBounds(Geometry geometry)
    {
        var result = new List<PreviewRectangle>();
        CollectObstacleBounds(geometry, result);
        return result;
    }

    private void CollectObstacleBounds(Geometry geometry, ICollection<PreviewRectangle> result)
    {
        if (geometry is GeometryGroup group)
        {
            foreach (var child in group.Children)
            {
                CollectObstacleBounds(child, result);
            }

            return;
        }

        var bounds = geometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var rectangle = new PreviewRectangle(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height);
        if (!rectangle.IsEmpty)
        {
            result.Add(rectangle);
        }
    }

    private PreviewRectangle GetResizeBounds(
        PreviewRectangle current,
        PreviewRectangle available,
        IReadOnlyList<PreviewRectangle> obstacles)
    {
        var left = available.Left;
        var top = available.Top;
        var right = available.Right;
        var bottom = available.Bottom;

        var gap = ToDeviceSize(LayoutGap, LayoutGap).Width;
        foreach (var obstacle in obstacles.Select(item => item.Inflate(gap)))
        {
            var overlapsVertically = current.Top < obstacle.Bottom && obstacle.Top < current.Bottom;
            if (overlapsVertically && obstacle.Right <= current.Left)
            {
                left = Math.Max(left, obstacle.Right);
            }
            else if (overlapsVertically && obstacle.Left >= current.Right)
            {
                right = Math.Min(right, obstacle.Left);
            }

            var overlapsHorizontally = current.Left < obstacle.Right && obstacle.Left < current.Right;
            if (overlapsHorizontally && obstacle.Bottom <= current.Top)
            {
                top = Math.Max(top, obstacle.Bottom);
            }
            else if (overlapsHorizontally && obstacle.Top >= current.Bottom)
            {
                bottom = Math.Min(bottom, obstacle.Top);
            }
        }

        return new PreviewRectangle(
            left,
            top,
            Math.Max(1, right - left),
            Math.Max(1, bottom - top));
    }

    private Geometry CreateReservation(PreviewRectangle anchor, PreviewRectangle popup)
    {
        var group = new GeometryGroup();
        group.Children.Add(new RectangleGeometry(ToRect(anchor)));
        group.Children.Add(new RectangleGeometry(ToRect(popup)));
        return group;
    }

    private static Rect ToRect(PreviewRectangle rectangle) =>
        new(rectangle.Left, rectangle.Top, rectangle.Width, rectangle.Height);

    /// <summary>
    /// 取一次 DIP 與實體像素的轉換矩陣，供這一輪定位重複使用。
    /// </summary>
    /// <remarks>
    /// 一次定位會換算七、八回，每回都重查 <see cref="PresentationSource.FromVisual"/>
    /// 等於在每一次捲動與每一個按鍵上重走同一段視覺樹。DPI 只有在視窗換螢幕或系統
    /// 縮放改變時才會變，而那兩件事都會先觸發一次版面重算，也就一定會先走到這裡。
    /// </remarks>
    private void RefreshDeviceTransforms()
    {
        _toDevice = NativeScreen.GetTransformToDevice(_view.VisualElement);
        _fromDevice = NativeScreen.GetTransformFromDevice(_view.VisualElement);
    }

    private Size ToDeviceSize(double width, double height) => Transform(_toDevice, width, height);

    private Size ToLogicalSize(double width, double height) => Transform(_fromDevice, width, height);

    private static Size Transform(Matrix matrix, double width, double height)
    {
        var vector = matrix.Transform(new Vector(width, height));
        return new Size(Math.Abs(vector.X), Math.Abs(vector.Y));
    }

    private void AttachEvents()
    {
        if (_eventsAttached)
        {
            return;
        }

        _eventsAttached = true;
        _control.GotFocus += OnContentGotFocus;
        _control.LostFocus += OnContentLostFocus;
        _control.InteractionFocusGained += OnInteractionFocusGained;
        _control.InteractionFocusLost += OnInteractionFocusLost;
        _view.LostAggregateFocus += OnViewLostAggregateFocus;
        _hostWindow = Window.GetWindow(_view.VisualElement);
        if (_hostWindow is not null)
        {
            _hostWindow.LocationChanged += OnHostWindowLocationChanged;
        }
    }

    private void DetachEvents()
    {
        if (!_eventsAttached)
        {
            return;
        }

        _eventsAttached = false;
        _control.GotFocus -= OnContentGotFocus;
        _control.LostFocus -= OnContentLostFocus;
        _control.InteractionFocusGained -= OnInteractionFocusGained;
        _control.InteractionFocusLost -= OnInteractionFocusLost;
        _view.LostAggregateFocus -= OnViewLostAggregateFocus;
        if (_hostWindow is not null)
        {
            _hostWindow.LocationChanged -= OnHostWindowLocationChanged;
            _hostWindow = null;
        }
    }

    private void OnContentGotFocus(object sender, RoutedEventArgs eventArgs) =>
        GotFocus?.Invoke(sender, eventArgs);

    private void OnContentLostFocus(object sender, RoutedEventArgs eventArgs) =>
        LostFocus?.Invoke(sender, eventArgs);

    private void OnInteractionFocusGained(object? sender, EventArgs eventArgs) =>
        GotFocus?.Invoke(sender, eventArgs);

    private void OnInteractionFocusLost(object? sender, EventArgs eventArgs) =>
        LostFocus?.Invoke(sender, eventArgs);

    private void OnViewLostAggregateFocus(object sender, EventArgs eventArgs)
    {
        if (_disposed || !_popup.IsOpen || _focusCheckQueued)
        {
            return;
        }

        _focusCheckQueued = true;
        _view.VisualElement.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => SqlAssistPlatformGuard.Run(
                "失焦時收起結構預覽",
                () =>
                {
                    _focusCheckQueued = false;
                    if (!_disposed && _popup.IsOpen && !HasFocus)
                    {
                        _manager.RemoveAgent(this);
                    }
                })));
    }

    private void OnHostWindowLocationChanged(object? sender, EventArgs eventArgs) => RequestReposition();

    private void LogPlacement(PreviewRectangle anchor)
    {
        // 這條在每一次捲動與每一個按鍵上都會走到。SqlAssistDiagnostics.Write 自己也會
        // 檢查一次，但那時字串與兩次 DPI 換算都已經做完了，等於白付一輪成本。
        if (_isResizing || !SqlAssistSettingsStore.Current.VerboseLogging)
        {
            return;
        }

        var popupScreen = ToRect(_bounds);
        var anchorScreen = ToRect(anchor);
        var effectiveSize = ToLogicalSize(_bounds.Width, _bounds.Height);
        var dpiSize = ToDeviceSize(96, 96);
        SqlAssistDiagnostics.Write(
            $"結構預覽落點：{_side}　" +
            $"視窗 {popupScreen.Left:F0},{popupScreen.Top:F0}–{popupScreen.Right:F0},{popupScreen.Bottom:F0}　" +
            $"錨點 {anchorScreen.Left:F0},{anchorScreen.Top:F0}　" +
            $"文件 {_availableBounds.Left:F0},{_availableBounds.Top:F0}–{_availableBounds.Right:F0},{_availableBounds.Bottom:F0}　" +
            $"有效 {effectiveSize.Width:F0}×{effectiveSize.Height:F0} DIP　" +
            $"DPI {dpiSize.Width:F0}×{dpiSize.Height:F0}　Zoom {_view.ZoomLevel:F0}%　" +
            $"保留區 {_obstacles.Count}　fallback {_usedFallback}　" +
            $"受限 寬 {WidthConstrained}／高 {HeightConstrained}",
            _view);
    }

    private sealed class ExactPopup : Popup
    {
        protected override void OnOpened(EventArgs eventArgs)
        {
            base.OnOpened(eventArgs);
            if (Child is Visual visual)
            {
                NativeScreen.SetNoTopmost(visual);
            }
        }
    }
}
