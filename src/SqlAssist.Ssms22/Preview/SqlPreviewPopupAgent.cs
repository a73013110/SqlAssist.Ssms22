using System;
using System.Collections.Generic;
using System.ComponentModel;
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
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 可確定定位的空間保留代理人。
/// </summary>
/// <remarks>
/// 平台內建 PopupAgent 會依 Windows 的左右手功能表設定改用 PlacementMode.Left，
/// 畫面因此可能與它回報給 reservation stack 的矩形相反。這裡仍加入同一套
/// ISpaceReservationManager 以保留聚合焦點，但用 Relative 明確套用座標。
///
/// 兩種擺法：錨在名稱上時由 <see cref="PreviewPlacementEngine"/> 算在錨點上下，尺寸是使用者
/// 記住的那一份；釘住之後矩形整個歸使用者，以編輯器左上角為基準記住，捲動、打字都不動它。
///
/// 這一層不決定去留：編輯器被藏起來（切分頁）或版面暫時放不下時只把 Popup 藏起來、留著
/// Agent，回來就照原樣出現；錨點捲出畫面時同樣先藏起來，再發 <see cref="AnchorScrolledOut"/>
/// 讓 <see cref="SqlStructurePreview"/> 問生命週期規則。以前這裡在編輯器失焦後檢查鍵盤焦點並
/// 自行移除，結果取決於那一瞬間焦點在誰身上——切到別的程式時，點過預覽與沒點過的收法不同。
/// </remarks>
internal sealed class SqlPreviewPopupAgent : ISpaceReservationAgent, IDisposable
{
    /// <summary>
    /// 與錨點、建議清單之間的間距。
    /// </summary>
    /// <remarks>
    /// 0 是因為視窗外圈本來就留了一圈透明的陰影邊（<see cref="PreviewSurface.ShadowMargin"/>），
    /// 看得見的表面與鄰居之間已經隔著那一圈；再加就是兩份間距。
    /// </remarks>
    private const double LayoutGap = 0;
    private const double BoundsPadding = 4;

    private readonly IWpfTextView _view;
    private readonly ISpaceReservationManager _manager;
    private readonly PreviewSurface _surface;
    private readonly ContentControl _container;
    private readonly ExactPopup _popup;

    private ITrackingSpan _anchor;
    private PreviewPreferredSize _preferred = PreviewPreferredSize.Default;
    private IReadOnlyList<PreviewRectangle> _obstacles = Array.Empty<PreviewRectangle>();
    private PreviewRectangle _availableBounds;
    private PreviewRectangle _bounds;
    private PreviewPlacementSide _side;
    private bool _eventsAttached;
    private bool _hasLayout;

    /// <summary>
    /// 釘住的視窗在編輯器裡的位置與大小（DIP，以編輯器左上角為基準）；沒釘住時 null。
    /// </summary>
    /// <remarks>
    /// 以編輯器為基準而不是螢幕：SSMS 視窗搬動或分割線拉動時跟著編輯器走，捲動則完全不動。
    /// 放不下時顯示的是收進界內的矩形，這一份不改，編輯器回到原本大小時視窗也回到原處。
    /// </remarks>
    private Rect? _pinned;

    /// <summary>上一輪定位時的錨點矩形；錨點沒動時，滑鼠停留提示不能把預覽推開。</summary>
    private PreviewRectangle _layoutAnchor;

    /// <summary>這一次錨點已經捲出畫面、也已經通知過；錨點回來才重新計算。</summary>
    private bool _anchorOutOfView;

    /// <summary>
    /// 正在換位置：舊位置縮回膠囊的途中，收完才換到這個錨點重新長出來；平時 null。
    /// </summary>
    /// <remarks>
    /// 縮回的途中不重新定位：錨點已經換了，照新錨點重算會讓正在縮的那一扇先跳到新位置再縮。
    /// </remarks>
    private ITrackingSpan? _relocation;

    /// <summary>換位置收完時要做的事；連續換了幾次名稱，只做最後一次交代的。</summary>
    private Action? _relocated;

    /// <summary>上一輪定位時編輯器左上角的螢幕位置；SSMS 視窗搬動時照它的位移同步平移。</summary>
    private Point _layoutOrigin;

    private PreviewDragHandle? _dragHandle;
    private PreviewRectangle _dragStartBounds;
    private PreviewRectangle _dragLimits;
    private bool _dragUpdateQueued;
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

    /// <summary>文件欄底界是誰算出來的；認錯了只會表現成「預覽矮了一截」，沒有別的徵兆。</summary>
    private string _documentColumnSource = "none";

    /// <summary>已經回報過內容掛在別的承載視窗上；這件事會每一次重排都成立一次。</summary>
    private bool _reportedDetachedContent;

    public SqlPreviewPopupAgent(
        IWpfTextView view,
        ISpaceReservationManager manager,
        ITrackingSpan anchor,
        PreviewSurface surface)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));

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

        // 切到別的程式再點回預覽時，Popup 不會把 SSMS 帶回前景，要鍵盤的操作全部失效；
        // 在內容處理這一次按下之前先啟用編輯器所在的框架。理由見 SsmsWindows.ActivateFrameOf。
        _container.PreviewMouseDown += (_, _) => SqlAssistPlatformGuard.Run(
            "點預覽時帶回前景",
            () => SsmsWindows.ActivateFrameOf(_view.VisualElement));
    }

    public bool IsMouseOver => _popup.IsOpen && (_surface.IsMouseOver || _surface.HasOpenContextMenu);

    public bool HasFocus =>
        _popup.IsOpen && (_popup.IsKeyboardFocusWithin || _surface.HasOpenContextMenu);

    public double CurrentWidth => ToLogicalSize(_bounds.Width, _bounds.Height).Width;

    public double CurrentHeight => ToLogicalSize(_bounds.Width, _bounds.Height).Height;

    public bool IsPinned => _pinned is not null;

    /// <summary>錨點目前在畫面外；收到 <see cref="AnchorScrolledOut"/> 之後再確認一次用。</summary>
    public bool IsAnchorOutOfView => _anchorOutOfView && _pinned is null;

    /// <summary>
    /// 捲動讓錨點離開了畫面；一次捲出只發一次。
    /// </summary>
    /// <remarks>
    /// 在定位的途中發出，平台還在重排整個保留區堆疊；收到的一方不能當場移除 Agent，
    /// 要排到派送佇列之後再處理。
    /// </remarks>
    public event EventHandler? AnchorScrolledOut;

    /// <summary>平台靠這兩個事件算編輯器的聚合焦點；右鍵選單是另一個 Popup，也要算進來。</summary>
    public event EventHandler? LostFocus;

    public event EventHandler? GotFocus;

    /// <summary>下一次被收起時要不要縮回錨點；清單上路過關鍵字而暫時收起時不必。</summary>
    public bool AnimateNextHide { get; set; }

    /// <summary>
    /// 更新錨點、記住的尺寸與是否釘住。
    /// </summary>
    /// <remarks>
    /// 釘住的那一刻以眼前的矩形為起點：使用者按圖釘或開始拖抬頭時看到的就是它，不能跳到別處。
    /// 範圍也在這一刻換成整個 SSMS 視窗：拖抬頭是先釘住、同一個按下就開始搬，等不到下一輪定位；
    /// 沿用錨在名稱上時量的文件欄，第一次拖抬頭就被關在查詢視窗裡，要先按圖釘才拖得出去。
    /// 放開圖釘回到錨在名稱上的擺法，下一輪定位重新從錨點算起。
    /// </remarks>
    public void Update(ITrackingSpan anchor, PreviewPreferredSize preferred, bool pinned)
    {
        if (anchor is null)
        {
            throw new ArgumentNullException(nameof(anchor));
        }

        if (_relocation is not null)
        {
            _relocation = anchor;
        }
        else
        {
            _anchor = anchor;
        }

        _preferred = preferred;
        if (pinned == _pinned is not null)
        {
            return;
        }

        // 換擺法之後錨點要重新看一次；沿用舊的「已經通知過」會讓放開圖釘時錨點不在畫面上的
        // 視窗一直藏著，既不出現也不收。
        _anchorOutOfView = false;
        if (!pinned)
        {
            _pinned = null;
            _hasLayout = false;
            return;
        }

        if (_hasLayout && !_bounds.IsEmpty)
        {
            RefreshDeviceTransforms();
            _pinned = ToEditorRect(_bounds);
            _availableBounds = GetPinnedBounds(_bounds.Left, _bounds.Top);
        }
    }

    /// <summary>
    /// 換到另一個名稱旁邊：舊位置縮回寫著新名稱的膠囊，收完再從新錨點長出來。
    /// </summary>
    /// <remarks>
    /// 直接重新定位的那一版，整扇窗一格之內跳到別處，眼睛追不上是同一扇窗換了位置還是又開了一扇。
    /// 還沒顯示、動畫關著或釘住時直接換錨點。
    /// </remarks>
    /// <param name="collapsed">舊位置收完、新位置長出來之前；呼叫端在這時才換上新內容。</param>
    public void Relocate(ITrackingSpan anchor, Action collapsed)
    {
        if (anchor is null)
        {
            throw new ArgumentNullException(nameof(anchor));
        }

        if (collapsed is null)
        {
            throw new ArgumentNullException(nameof(collapsed));
        }

        _relocated = collapsed;
        if (_relocation is not null)
        {
            _relocation = anchor;
            return;
        }

        if (!_popup.IsOpen || _pinned is not null || !SqlAssistChrome.MotionEnabled ||
            !ReferenceEquals(_container.Content, _surface))
        {
            _anchor = anchor;
            _relocated = null;
            collapsed();
            RequestReposition();
            return;
        }

        _relocation = anchor;
        _surface.PlayExit(relocating: true, () =>
        {
            if (_relocation is not { } next || _disposed)
            {
                return;
            }

            // 收完才換：先藏起來，下一輪定位從新錨點算，Popup 重新打開時照常長出來。
            var done = _relocated;
            _relocation = null;
            _relocated = null;
            _anchor = next;
            _hasLayout = false;
            _anchorOutOfView = false;
            Suspend();
            done?.Invoke();
            RequestReposition();
        });
    }

    /// <summary>正在舊位置縮回膠囊；這段時間換上的內容要等收完。</summary>
    public bool IsRelocating => _relocation is not null;

    /// <summary>釘住的視窗換成指定尺寸，左上角不動；雙擊握把重設時用。</summary>
    public void ResizePinned(PreviewPreferredSize size)
    {
        if (_pinned is not { } pinned)
        {
            return;
        }

        _pinned = new Rect(pinned.Left, pinned.Top, size.WidthOrDefault, size.Height);
        RequestReposition();
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

    /// <summary>回報這一輪佔用的幾何；<c>null</c> 代表請空間管理員收掉這個 Agent。</summary>
    /// <remarks>
    /// SSMS 22.10 的組件把整個 <c>ISpaceReservationAgent</c> 標成不可為 NULL，但
    /// <c>null</c>（收掉）與 <see cref="Geometry.Empty"/>（留著，這一輪不畫）在管理員
    /// 眼裡是兩件事，本檔案兩種都用得到。跟著改成不可為 NULL 會把「收掉」變成「留著」，
    /// 那是行為變更而不是修警告，因此只在這一個成員抑制註解不符。
    /// </remarks>
#pragma warning disable CS8766
    public Geometry? PositionAndDisplay(Geometry reservedSpace) =>
        SqlAssistPlatformGuard.Run<Geometry?>(
            "定位結構預覽",
            () => PositionAndDisplayCore(reservedSpace),
            fallback: null);
#pragma warning restore CS8766

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

        if (!_view.VisualElement.IsVisible)
        {
            // 整個編輯器被藏起來（切到別的分頁）：先藏起來，回來再出現。回報 null 會讓
            // 平台移除 Agent，那等於把「暫時看不見」當成使用者不要了。
            Suspend();
            return Geometry.Empty;
        }

        RefreshDeviceTransforms();
        _layoutOrigin = _view.VisualElement.PointToScreen(new Point(0, 0));
        if (_relocation is not null && _hasLayout)
        {
            Display(_bounds);
            return CreateReservation(anchor: null, _bounds);
        }

        return _pinned is { } pinned
            ? PositionPinned(pinned)
            : PositionAtAnchor(reservedSpace);
    }

    /// <summary>釘住：矩形歸使用者，只收進 SSMS 視窗，不看錨點也不讓開別的浮窗。</summary>
    private Geometry PositionPinned(Rect pinned)
    {
        if (_dragHandle is null)
        {
            var screen = FromEditorRect(pinned);
            _availableBounds = GetPinnedBounds(screen.Left, screen.Top);
            var minimum = ToDeviceSize(SqlAssistLimits.MinimumPreviewWidth, SqlAssistLimits.MinimumPreviewHeight);
            _bounds = PreviewDragEngine.Contain(screen, _availableBounds, minimum.Width, minimum.Height);
            _hasLayout = true;
        }

        _surface.SetResizeGrips(onTop: false, pinned: true);
        Display(_bounds);
        LogPlacement(anchor: null);
        return CreateReservation(anchor: null, _bounds);
    }

    /// <summary>錨在名稱上：擺在錨點上下，避開建議清單與提示。</summary>
    private Geometry PositionAtAnchor(Geometry reservedSpace)
    {
        if (TryGetAnchorBounds() is not { } anchorBounds)
        {
            // 捲出畫面：先藏起來，要不要收交給生命週期（清單上展開的不收，清單自己也藏了）。
            Suspend();
            if (!_anchorOutOfView)
            {
                _anchorOutOfView = true;
                AnchorScrolledOut?.Invoke(this, EventArgs.Empty);
            }

            return Geometry.Empty;
        }

        _anchorOutOfView = false;
        if (_hasLayout && _dragHandle is null && anchorBounds == _layoutAnchor && IsQuickInfoOpen())
        {
            // 滑鼠停留提示排在預覽前面，它一出現就會變成預覽要讓開的障礙，而使用者只是
            // 在別的名稱上停了一下。錨點沒動就留在原地，提示自己會在滑鼠離開時消失。
            Display(_bounds);
            return CreateReservation(anchorBounds, _bounds);
        }

        if (_dragHandle is null)
        {
            _obstacles = GetObstacleBounds(reservedSpace);
            _availableBounds = GetAvailableBounds(anchorBounds.Left, anchorBounds.Top);
            var layout = CalculateLayout(anchorBounds);
            if (layout.Bounds.IsEmpty)
            {
                Suspend();
                return Geometry.Empty;
            }

            _layoutAnchor = anchorBounds;
            _bounds = layout.Bounds;
            _side = layout.Side;
            _hasLayout = true;
            _surface.SetResizeGrips(onTop: _side == PreviewPlacementSide.Above, pinned: false);
        }

        Display(_bounds);
        LogPlacement(anchorBounds);
        return CreateReservation(anchorBounds, _bounds);
    }

    private PreviewLayout CalculateLayout(PreviewRectangle anchorBounds)
    {
        var desiredSize = ToDeviceSize(_preferred.WidthOrDefault, _preferred.Height);
        var minimumSize = ToDeviceSize(
            SqlAssistLimits.MinimumPreviewWidth,
            SqlAssistLimits.MinimumPreviewHeight);
        var absoluteMaximumSize = ToDeviceSize(
            SqlAssistLimits.MaximumPreviewWidth,
            SqlAssistLimits.MaximumPreviewHeight);
        return PreviewPlacementEngine.Calculate(
            new PreviewLayoutRequest
            {
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
                StretchWidth = !_preferred.Width.HasValue,
                Gap = ToDeviceSize(LayoutGap, LayoutGap).Width,
                PreviousSide = _hasLayout ? _side : (PreviewPlacementSide?)null
            });
    }

    /// <summary>平台移除 Agent 時呼叫；要動畫就先縮回錨點，收完才放開內容。</summary>
    public void Hide()
    {
        _surface.CloseTransientPopups();
        DetachEvents();
        _relocation = null;
        _relocated = null;
        if (AnimateNextHide && _popup.IsOpen && ReferenceEquals(_container.Content, _surface))
        {
            AnimateNextHide = false;
            _surface.PlayExit(relocating: false, ReleaseContent);
            return;
        }

        ReleaseContent();
    }

    /// <summary>看不見時暫時藏起來；Agent 與內容都留著，下一輪定位成功就再出現。</summary>
    private void Suspend()
    {
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
        }
    }

    private void ReleaseContent()
    {
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
        }

        if (ReferenceEquals(_container.Content, _surface))
        {
            _container.Content = null;
        }

        if (_disposed)
        {
            _popup.Child = null;
        }
    }

    private bool IsQuickInfoOpen() =>
        SqlPreviewServices.Current is { } services && services.IsQuickInfoOpen(_view);

    /// <summary>
    /// 開始拖抬頭或角落握把；拖曳期間不重新定位，矩形只由起點與總位移決定。
    /// </summary>
    /// <remarks>
    /// 錨在名稱上時，握把只能拖到別的浮窗（建議清單、提示）為止，放開之後定位才會落在同一個
    /// 地方；釘住的視窗不讓開任何浮窗，範圍就是整個 SSMS 視窗（<see cref="GetPinnedBounds"/>）。
    /// </remarks>
    public void BeginDrag(PreviewDragHandle handle)
    {
        if (_bounds.IsEmpty)
        {
            return;
        }

        // DPI 也在這裡凍結一次，與控制項那一端同步。
        RefreshDeviceTransforms();
        _dragHandle = handle;
        _dragStartBounds = _bounds;
        _dragLimits = _pinned is null
            ? GetResizeLimits(_bounds, _availableBounds, _obstacles)
            : _availableBounds;
        _pendingHorizontalChange = 0;
        _pendingVerticalChange = 0;
    }

    /// <summary>位移量一律是相對按下瞬間的總量，不從上一幀累加；同一幀只套用一次。</summary>
    public void Drag(double horizontalChange, double verticalChange)
    {
        if (_dragHandle is null)
        {
            return;
        }

        _pendingHorizontalChange = horizontalChange;
        _pendingVerticalChange = verticalChange;
        if (_dragUpdateQueued)
        {
            return;
        }

        _dragUpdateQueued = true;
        _view.VisualElement.Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() => SqlAssistPlatformGuard.Run(
                "更新結構預覽拖曳",
                () =>
                {
                    _dragUpdateQueued = false;
                    if (_dragHandle is not null)
                    {
                        ApplyPendingDrag();
                    }
                })));
    }

    private void ApplyPendingDrag()
    {
        if (_dragHandle is not { } handle)
        {
            return;
        }

        var minimumSize = ToDeviceSize(
            SqlAssistLimits.MinimumPreviewWidth,
            SqlAssistLimits.MinimumPreviewHeight);
        var maximumSize = ToDeviceSize(
            SqlAssistLimits.MaximumPreviewWidth,
            SqlAssistLimits.MaximumPreviewHeight);
        _bounds = PreviewDragEngine.Drag(
            _dragStartBounds,
            handle,
            _pendingHorizontalChange,
            _pendingVerticalChange,
            _dragLimits,
            minimumSize.Width,
            minimumSize.Height,
            maximumSize.Width,
            maximumSize.Height);
        Display(_bounds);

        // 自訂 agent 在拖曳中維持同一個 Rect；重排只更新其他 agent 看見的保留區。
        RequestReposition();
    }

    public void CompleteDrag(bool canceled)
    {
        if (_dragHandle is null)
        {
            return;
        }

        if (canceled)
        {
            _bounds = _dragStartBounds;
            Display(_bounds);
        }
        else
        {
            // DragCompleted 可能早於最後一個 Render callback；收尾前先同步套用最後總位移。
            ApplyPendingDrag();
        }

        _dragHandle = null;
        if (_pinned is not null)
        {
            _pinned = ToEditorRect(_bounds);
        }

        RequestReposition();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dragHandle = null;

        // 平台移除時通常已經呼叫過 Hide；縮回錨點的動畫還在跑時由它收完再放開內容。
        if (_surface.IsExiting)
        {
            return;
        }

        if (_popup.IsOpen)
        {
            Hide();
        }
        else
        {
            ReleaseContent();
        }
    }

    private void Display(PreviewRectangle bounds)
    {
        var logicalSize = ToLogicalSize(bounds.Width, bounds.Height);
        var relativeLocation = _view.VisualElement.PointFromScreen(
            new Point(bounds.Left, bounds.Top));
        _surface.SetEffectiveSize(logicalSize.Width, logicalSize.Height);
        _popup.HorizontalOffset = relativeLocation.X;
        _popup.VerticalOffset = relativeLocation.Y;

        if (_container.Content is null && VisualTreeHelper.GetParent(_surface) is null)
        {
            _container.Content = _surface;
        }

        if (ReferenceEquals(_container.Content, _surface))
        {
            if (!_popup.IsOpen)
            {
                AttachEvents();
                _popup.IsOpen = true;
                PlayEnter(bounds);
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

    /// <summary>從錨點下方長出來；釘住的視窗沒有錨點，從左上角長出。</summary>
    private void PlayEnter(PreviewRectangle bounds)
    {
        if (_pinned is not null)
        {
            _surface.PlayEnter(origin: 0, fromBottom: false);
            return;
        }

        var offset = ToLogicalSize(Math.Max(0, _layoutAnchor.Left - bounds.Left), 0).Width;
        _surface.PlayEnter(
            origin: Math.Max(0, offset - PreviewSurface.ShadowMargin),
            fromBottom: _side == PreviewPlacementSide.Above);
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
    private PreviewRectangle GetAvailableBounds(double screenX, double screenY)
    {
        var visual = _view.VisualElement;
        var editor = GetScreenBounds(visual);
        var left = editor.Left;
        var top = editor.Top;
        var right = editor.Right;
        var devicePadding = ToDeviceSize(BoundsPadding, BoundsPadding);
        var bottom = ResolveDocumentColumnBottom(visual, editor);

        if (NativeScreen.TryGetWorkArea(new Point(screenX, screenY)) is { } workArea)
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
    /// 釘住的窗可以擺到哪裡：編輯器所在的整個 SSMS 視窗（含物件總管與底部工具窗），收進螢幕工作區。
    /// </summary>
    /// <remarks>
    /// 錨在名稱上的預覽限在文件欄，是因為它不該蓋住使用者沒叫它蓋的東西；釘住的是使用者自己擺的，
    /// 擺到物件總管上面正是常見的用法。再往外（別的螢幕、別的程式上面）就交給「移到工具視窗」：
    /// Popup 屬於編輯器，跨出 SSMS 視窗的那一塊在編輯器被遮住或縮小時會懸在半空中。
    /// </remarks>
    private PreviewRectangle GetPinnedBounds(double screenX, double screenY)
    {
        var host = _hostWindow ?? SsmsWindows.WindowOf(_view.VisualElement);
        if (host?.Content is not FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } client)
        {
            return GetAvailableBounds(screenX, screenY);
        }

        var bounds = GetScreenBounds(client);
        var left = bounds.Left;
        var top = bounds.Top;
        var right = bounds.Right;
        var bottom = bounds.Bottom;
        if (NativeScreen.TryGetWorkArea(new Point(screenX, screenY)) is { } workArea)
        {
            var devicePadding = ToDeviceSize(BoundsPadding, BoundsPadding);
            left = Math.Max(left, workArea.Left + devicePadding.Width);
            top = Math.Max(top, workArea.Top + devicePadding.Height);
            right = Math.Min(right, workArea.Right - devicePadding.Width);
            bottom = Math.Min(bottom, workArea.Bottom - devicePadding.Height);
        }

        return new PreviewRectangle(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
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

        // 比對邊界一律用含裝飾邊的編輯器控制項，不能用文字 Viewport：後者的左界不含
        // 中斷點欄、行號欄與變更欄，右界不含捲軸，加起來超過任何合理容忍值，於是
        // 每一個祖先都被判成不是同一欄，文件欄永遠找不到——症狀就是查詢一有結果，
        // 預覽的底就卡在文字區底部，蓋不到結果窗格。
        var chrome = ResolveEditorChrome(visual);
        var hasChrome = !ReferenceEquals(chrome, visual);
        var column = hasChrome ? GetScreenBounds(chrome) : editor;
        var left = column.Left;
        var top = column.Top;
        var right = column.Right;
        var columnBottom = Math.Max(column.Bottom, editor.Bottom);
        var bottom = editor.Bottom;

        // 找得到編輯器控制項時左右界已經吻合，只需容忍邊框與 DPI 捨入；認不出來時
        // 基準退回文字 Viewport，容忍值就得放到整條左側裝飾邊之外。
        var toleranceUnits = hasChrome ? 24 : 96;
        var ancestorTolerance = ToDeviceSize(toleranceUnits, toleranceUnits);
        var expandedByWpfParent = false;
        var minimumUsefulExpansion = Math.Max(8, ancestorTolerance.Height / 2);
        _documentColumnSource = hasChrome ? "none/chrome" : "none/view";

        for (DependencyObject? current = VisualTreeHelper.GetParent(chrome);
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
                ancestorBottomRight.Y > columnBottom + minimumUsefulExpansion)
            {
                bottom = Math.Max(bottom, ancestorBottomRight.Y);
                expandedByWpfParent = true;
                _documentColumnSource = "wpf";

                // 最近一個向下擴張的同欄父容器就是 editor/results splitter；不再爬到 Shell root。
                break;
            }
        }

        var columnBounds = new Rect(left, top, right - left, columnBottom - top);
        if (!expandedByWpfParent &&
            NativeScreen.TryGetDocumentColumnBottom(
                visual,
                columnBounds,
                ancestorTolerance.Width) is { } nativeBottom)
        {
            bottom = Math.Max(bottom, nativeBottom);
            _documentColumnSource = "win32";
        }

        _documentColumnEditor = editor;
        _documentColumnBottom = bottom;
        _hasDocumentColumn = true;
        return bottom;
    }

    /// <summary>
    /// 含中斷點欄、行號欄與捲軸的編輯器控制項；認不出來時回傳原本的文字 Viewport。
    /// </summary>
    /// <remarks>
    /// 這裡刻意找實作 <see cref="IWpfTextViewHost"/> 的那一層，而不是數幾層祖先或比對
    /// 型別名稱：編輯器的內部視覺結構會隨版本增減層數，而這個介面是公開契約。
    /// </remarks>
    private static FrameworkElement ResolveEditorChrome(FrameworkElement visual)
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(visual);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is Window)
            {
                break;
            }

            if (current is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } element &&
                current is IWpfTextViewHost)
            {
                return element;
            }
        }

        return visual;
    }

    private static PreviewRectangle GetScreenBounds(FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(
            new Point(Math.Max(1, element.ActualWidth), Math.Max(1, element.ActualHeight)));
        return new PreviewRectangle(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Max(1, Math.Abs(bottomRight.X - topLeft.X)),
            Math.Max(1, Math.Abs(bottomRight.Y - topLeft.Y)));
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

    /// <summary>錨在名稱上時握把拖得到的範圍：文件欄，再扣掉四周的浮窗。</summary>
    private PreviewRectangle GetResizeLimits(
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

    private static Geometry CreateReservation(PreviewRectangle? anchor, PreviewRectangle popup)
    {
        var group = new GeometryGroup();
        if (anchor is { } value)
        {
            group.Children.Add(new RectangleGeometry(ToRect(value)));
        }

        group.Children.Add(new RectangleGeometry(ToRect(popup)));
        return group;
    }

    /// <summary>螢幕實體像素的矩形換成以編輯器左上角為基準的 DIP；釘住的位置這樣記。</summary>
    private Rect ToEditorRect(PreviewRectangle screen)
    {
        var topLeft = _view.VisualElement.PointFromScreen(new Point(screen.Left, screen.Top));
        var size = ToLogicalSize(screen.Width, screen.Height);
        return new Rect(topLeft, size);
    }

    private PreviewRectangle FromEditorRect(Rect editor)
    {
        var topLeft = _view.VisualElement.PointToScreen(editor.TopLeft);
        var size = ToDeviceSize(editor.Width, editor.Height);
        return new PreviewRectangle(topLeft.X, topLeft.Y, size.Width, size.Height);
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
        _surface.GotFocus += OnContentGotFocus;
        _surface.LostFocus += OnContentLostFocus;
        _surface.Panel.InteractionFocusGained += OnInteractionFocusGained;
        _surface.Panel.InteractionFocusLost += OnInteractionFocusLost;
        _view.VisualElement.IsVisibleChanged += OnViewVisibleChanged;
        _hostWindow = SsmsWindows.WindowOf(_view.VisualElement);
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
        _surface.GotFocus -= OnContentGotFocus;
        _surface.LostFocus -= OnContentLostFocus;
        _surface.Panel.InteractionFocusGained -= OnInteractionFocusGained;
        _surface.Panel.InteractionFocusLost -= OnInteractionFocusLost;
        _view.VisualElement.IsVisibleChanged -= OnViewVisibleChanged;
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

    /// <summary>切到別的分頁再切回來：藏起來的預覽跟著編輯器回來。</summary>
    private void OnViewVisibleChanged(object sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (_disposed)
        {
            return;
        }

        if (_view.VisualElement.IsVisible)
        {
            RequestReposition();
        }
        else
        {
            Suspend();
        }
    }

    /// <summary>
    /// SSMS 視窗搬動：當下就照編輯器的位移平移 Popup，放開之後再完整重排。
    /// </summary>
    /// <remarks>
    /// Popup 是自己的頂層視窗，不會跟著擁有者走；只排一次重排的那一版要等派送佇列輪到它，
    /// 拖著 SSMS 走時預覽落後一兩格，看起來像被橡皮筋拖著。位置本來就以編輯器為基準，
    /// 編輯器整塊跟著視窗走，所以照位移平移就是對的答案，不必重算版面。改變大小（最大化、
    /// 貼齊）另有版面事件，照常完整重排。
    /// </remarks>
    private void OnHostWindowLocationChanged(object? sender, EventArgs eventArgs) =>
        SqlAssistPlatformGuard.Run("跟著 SSMS 視窗搬動結構預覽", () =>
        {
            if (_popup.IsOpen && _hasLayout && _dragHandle is null && !_disposed)
            {
                var origin = _view.VisualElement.PointToScreen(new Point(0, 0));
                var dx = origin.X - _layoutOrigin.X;
                var dy = origin.Y - _layoutOrigin.Y;
                if (dx != 0 || dy != 0)
                {
                    _layoutOrigin = origin;
                    _bounds = Offset(_bounds, dx, dy);
                    _layoutAnchor = Offset(_layoutAnchor, dx, dy);
                    NativeScreen.MoveWindowOf(_container, _bounds.Left, _bounds.Top);
                }
            }

            RequestReposition();
        });

    private static PreviewRectangle Offset(PreviewRectangle rectangle, double dx, double dy) =>
        new(rectangle.Left + dx, rectangle.Top + dy, rectangle.Width, rectangle.Height);

    [Localizable(false)]
    private void LogPlacement(PreviewRectangle? anchor)
    {
        // 這條在每一次捲動與每一個按鍵上都會走到。SqlAssistDiagnostics.Write 自己也會
        // 檢查一次，但那時字串與兩次 DPI 換算都已經做完了，等於白付一輪成本。
        if (_dragHandle is not null || !SqlAssistSettingsStore.Current.VerboseLogging)
        {
            return;
        }

        var popupScreen = ToRect(_bounds);
        var effectiveSize = ToLogicalSize(_bounds.Width, _bounds.Height);
        var dpiSize = ToDeviceSize(96, 96);
        var anchorText = anchor is { } value
            ? $"錨點 {value.Left:F0},{value.Top:F0}–{value.Right:F0},{value.Bottom:F0}　"
            : "釘住　";
        SqlAssistDiagnostics.Write(
            $"結構預覽落點：{_side}　" +
            $"視窗 {popupScreen.Left:F0},{popupScreen.Top:F0}–{popupScreen.Right:F0},{popupScreen.Bottom:F0}　" +
            anchorText +
            $"文件 {_availableBounds.Left:F0},{_availableBounds.Top:F0}–{_availableBounds.Right:F0},{_availableBounds.Bottom:F0}　" +
            $"底界來源 {_documentColumnSource}　" +
            $"有效 {effectiveSize.Width:F0}×{effectiveSize.Height:F0} DIP　" +
            $"DPI {dpiSize.Width:F0}×{dpiSize.Height:F0}　Zoom {_view.ZoomLevel:F0}%　" +
            $"保留區 {_obstacles.Count}{DescribeObstacles()}　記住的尺寸 {DescribePreferred()}",
            _view);
    }

    [Localizable(false)]
    private string DescribePreferred() =>
        $"{(_preferred.Width is { } width ? width.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "自動")}×{_preferred.Height:F0}";

    /// <summary>
    /// 保留區的逐一矩形。
    /// </summary>
    /// <remarks>
    /// 只記數量看不出預覽被誰擠矮了：建議清單本身、與它右側那塊沒有畫出來卻仍然被保留的
    /// 說明面板，在數字上完全一樣，而定位引擎兩者都當障礙。
    /// 字串只在詳細紀錄開著時才組，呼叫端已經先擋過一次。
    /// </remarks>
    [Localizable(false)]
    private string DescribeObstacles() =>
        _obstacles.Count == 0
            ? string.Empty
            : " [" +
              string.Join(
                  "；",
                  _obstacles.Select(obstacle =>
                      $"{obstacle.Left:F0},{obstacle.Top:F0}–{obstacle.Right:F0},{obstacle.Bottom:F0}")) +
              "]";

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
