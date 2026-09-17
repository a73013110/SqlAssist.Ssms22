using System;
using System.Windows;
using System.Windows.Controls;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Notifications;

/// <summary>
/// 通知卡片本身；整個處理程序只有一張，在宿主之間搬家。
/// </summary>
/// <remarks>
/// 提示同一時間只有一份、跟著作用中的宿主走，而卡片本來是每個編輯區各建一張。
/// 於是 F12 開新查詢視窗或切分頁時，同一份提示會整個重來：新卡片沒有列的身分，
/// 滑入、淡入與每一列的狀態動畫全部重播，看起來是提示消失了又跳出來。把卡片與它的
/// 可見期限留在這裡，換宿主就只是把同一張卡片換掛到另一層。
///
/// 卡片的輸入不轉給宿主：展開、關閉與暫停期限都是全域的，由控制器接。
///
/// 只在 UI 執行緒上讀寫：呼叫端全部來自控制器的刷新路徑與卡片自己的輸入事件。
/// </remarks>
internal sealed class NotificationSurface
{
    private readonly NotificationHandover _handover = new();
    private NotificationCard? _card;
    private INotificationSurfaceHost? _owner;

    /// <summary>按下抬頭或展開鈕。</summary>
    public event Action? DetailsToggled;

    /// <summary>按下關閉鈕。</summary>
    public event Action? DismissRequested;

    /// <summary>指標或鍵盤焦點進出卡片，期限要重新計算。</summary>
    public event Action? RetentionChanged;

    /// <summary>目前掛在畫面上的卡片；還沒有建立過時是 null。</summary>
    public NotificationCard? Card => _card;

    /// <summary>目前掛著卡片的宿主。</summary>
    public INotificationSurfaceHost? Owner => _owner;

    /// <summary>這一批從什麼時候開始看得到；換宿主不重新起算最短可見時間。</summary>
    public DateTimeOffset VisibleAt { get; private set; }

    /// <summary>淡出從什麼時候開始；沒有在淡出時是 null。</summary>
    public DateTimeOffset? HidingAt { get; set; }

    /// <summary>指標或鍵盤焦點還在卡片內，期限要暫停。</summary>
    public bool Retaining => _card is { } card && (card.IsMouseOver || card.IsKeyboardFocusWithin);

    public bool Attached => _owner is not null;

    public bool IsOwnedBy(INotificationSurfaceHost host) => ReferenceEquals(_owner, host);

    /// <summary>取得那一張卡片，必要時建立並接上輸入。</summary>
    public NotificationCard Acquire()
    {
        if (_card is { } existing) return existing;
        var card = SqlAssistChrome.CreateNotificationCard();
        // 還沒有人叫它出場；第一次掛上時由入場動畫從 0 帶到 1。
        card.Opacity = 0;
        // 事件只接一次：卡片不換，換的是掛著它的宿主。
        card.SummaryButton.Click += (_, args) => Raise(DetailsToggled, args);
        card.ToggleButton.Click += (_, args) => Raise(DetailsToggled, args);
        card.CloseButton.Click += (_, args) => Raise(DismissRequested, args);
        card.MouseEnter += (_, _) => RetentionChanged?.Invoke();
        card.MouseLeave += (_, _) => RetentionChanged?.Invoke();
        card.IsKeyboardFocusWithinChanged += (_, _) => RetentionChanged?.Invoke();
        card.SizeChanged += (_, _) => Place();
        VsThemeBrushes.Apply(card);
        _card = card;
        return card;
    }

    /// <summary>
    /// 把卡片擺到這個宿主上並掛上去。
    /// </summary>
    /// <returns>
    /// 這一次是不是「新出現在畫面上」。交接沿用同一張卡片，回 false——回 true 的那一次
    /// 才播入場動畫並重新起算最短可見時間。
    /// </returns>
    public bool Attach(INotificationSurfaceHost host, DateTimeOffset now)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        var card = Acquire();
        Place(host);
        if (IsOwnedBy(host)) return false;
        // 直接從別的宿主接手時 Release 之前就要先問，之後擁有者已經是 null 了。
        var taken = _owner is not null;
        Release();
        if (!host.Mount(card, () => OnRemoved(host))) return false;
        _owner = host;
        var fresh = _handover.Attach(taken, now);
        if (fresh) VisibleAt = now;
        return fresh;
    }

    /// <summary>
    /// 從目前的宿主拔下來。
    /// </summary>
    /// <param name="retire">
    /// 這一批結束了（到期、關閉、停用），不是交接。下一次出現要重新播入場動畫。
    /// </param>
    public void Detach(DateTimeOffset now, bool retire)
    {
        if (_owner is null) return;
        if (retire) _card?.StopMotion();
        Release();
        _handover.Detach(now, retire);
        HidingAt = null;
    }

    /// <summary>套件卸載：停掉動畫並收回卡片，不留下沒有人看的算繪。</summary>
    public void Shutdown()
    {
        _card?.StopMotion();
        Release();
        _handover.Reset();
        HidingAt = null;
    }

    /// <summary>依目前擁有者的範圍重新擺位；捲動與尺寸變化只走這一條。</summary>
    public void Place()
    {
        if (_owner is { } owner) Place(owner);
    }

    private void Place(INotificationSurfaceHost host)
    {
        if (_card is not { } card) return;
        var viewport = host.Viewport;
        card.Constrain(viewport.Size);
        card.Measure(new Size(card.MaxWidth, card.MaxHeight));
        var point = SqlAssistChrome.NotificationAnchor(viewport.Size, card.DesiredSize);
        Canvas.SetLeft(card, viewport.X + point.X);
        Canvas.SetTop(card, viewport.Y + point.Y);
    }

    /// <summary>把卡片從目前的宿主移除，但不改交接狀態。</summary>
    private void Release()
    {
        if (_owner is { } owner && _card is { } card)
        {
            // 先清掉擁有者：宿主移除時可能同步呼叫 removed，那一次不該再當成宿主自己收掉。
            _owner = null;
            owner.Unmount(card);
        }

        _owner = null;
    }

    /// <summary>宿主自己把卡片收掉了（編輯器關閉、換版面）。</summary>
    private void OnRemoved(INotificationSurfaceHost host)
    {
        if (IsOwnedBy(host)) _owner = null;
    }

    /// <remarks>例外由控制器的處理常式自己走 <c>SqlAssistPlatformGuard</c>；這裡只轉交。</remarks>
    private void Raise(Action? handler, RoutedEventArgs args)
    {
        if (_owner is null || handler is null) return;
        handler();
        args.Handled = true;
    }
}
