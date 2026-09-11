using System;
using System.Windows;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Editor;

/// <summary>掛著通知卡片的那一個編輯區要回答的幾件事。</summary>
/// <remarks>
/// 卡片只有一張、輸入卻要回到目前掛著它的那個編輯區，因此表面認的是這個介面而不是
/// <see cref="NotificationAdornment"/>：兩者互相持有具象型別的話，卡片的按鈕、指標與
/// 尺寸事件會把「該顯示什麼」與「擺哪裡」又混回同一個型別上。
/// </remarks>
internal interface INotificationSurfaceOwner
{
    /// <summary>提示錨在編輯區的哪一角。</summary>
    NotificationPosition Position { get; }

    /// <summary>展開或收合明細。</summary>
    void ToggleDetails();

    /// <summary>關閉目前這一批。</summary>
    void DismissBatch();

    /// <summary>指標或鍵盤焦點變了，期限要重新計算。</summary>
    void Invalidate();

    /// <summary>卡片大小變了，重新擺位。</summary>
    void Reposition();
}

/// <summary>
/// 通知卡片本身；整個處理程序只有一張，在編輯區之間搬家。
/// </summary>
/// <remarks>
/// 提示同一時間只有一份、跟著作用中的編輯區走，而卡片本來是每個編輯區各建一張。
/// 於是 F12 開新查詢視窗或切分頁時，同一份提示會整個重來：新卡片沒有列的身分，
/// 滑入、淡入與每一列的狀態動畫全部重播，看起來是提示消失了又跳出來。把卡片與它的
/// 可見期限留在這裡，換編輯區就只是把同一張卡片換掛到另一層。
///
/// 只在 UI 執行緒上讀寫：呼叫端全部來自編輯器的刷新路徑與卡片自己的輸入事件。
/// </remarks>
internal sealed class NotificationSurface
{
    public static NotificationSurface Default { get; } = new();

    private readonly NotificationHandover _handover = new();
    private NotificationCard? _card;
    private IAdornmentLayer? _layer;
    private INotificationSurfaceOwner? _owner;

    /// <summary>目前掛在畫面上的卡片；還沒有建立過時是 null。</summary>
    public NotificationCard? Card => _card;

    /// <summary>這一批從什麼時候開始看得到；換編輯區不重新起算最短可見時間。</summary>
    public DateTimeOffset VisibleAt { get; private set; }

    /// <summary>淡出從什麼時候開始；沒有在淡出時是 null。</summary>
    public DateTimeOffset? HidingAt { get; set; }

    /// <summary>指標或鍵盤焦點還在卡片內，期限要暫停。</summary>
    public bool Retaining => _card is { } card && (card.IsMouseOver || card.IsKeyboardFocusWithin);

    /// <summary>卡片現在掛在某一個編輯區上；是不是這一個要問 <see cref="IsOwnedBy"/>。</summary>
    public bool Attached => _layer is not null;

    public bool IsOwnedBy(INotificationSurfaceOwner owner) => ReferenceEquals(_owner, owner);

    /// <summary>取得那一張卡片，必要時建立並接上輸入。</summary>
    public NotificationCard Acquire()
    {
        if (_card is { } existing) return existing;
        var card = SqlAssistChrome.CreateNotificationCard();
        // 還沒有人叫它出場；第一次掛上時由入場動畫從 0 帶到 1。
        card.Opacity = 0;
        // 事件只接一次：卡片不換，換的是掛著它的編輯區，因此一律轉給目前的擁有者。
        card.SummaryButton.Click += (_, args) => Dispatch(owner => owner.ToggleDetails(), args);
        card.ToggleButton.Click += (_, args) => Dispatch(owner => owner.ToggleDetails(), args);
        card.CloseButton.Click += (_, args) => Dispatch(owner => owner.DismissBatch(), args);
        card.MouseEnter += (_, _) => _owner?.Invalidate();
        card.MouseLeave += (_, _) => _owner?.Invalidate();
        card.IsKeyboardFocusWithinChanged += (_, _) => _owner?.Invalidate();
        card.SizeChanged += (_, _) => _owner?.Reposition();
        VsThemeBrushes.Apply(card);
        _card = card;
        return card;
    }

    /// <summary>
    /// 把卡片掛到這一層。
    /// </summary>
    /// <returns>
    /// 這一次是不是「新出現在畫面上」。交接沿用同一張卡片，回 false——回 true 的那一次
    /// 才播入場動畫並重新起算最短可見時間。
    /// </returns>
    public bool Attach(INotificationSurfaceOwner owner, IAdornmentLayer layer, DateTimeOffset now)
    {
        if (owner is null) throw new ArgumentNullException(nameof(owner));
        if (layer is null) throw new ArgumentNullException(nameof(layer));
        var card = Acquire();
        if (IsOwnedBy(owner) && _layer is not null) return false;
        // 直接從別的編輯區接手時 Release 之前就要先問，之後 _layer 已經是 null 了。
        var taken = _layer is not null;
        Release();
        if (!layer.AddAdornment(AdornmentPositioningBehavior.ViewportRelative, null, owner, card,
                (_, _) => OnLayerRemoved(owner)))
            return false;
        _owner = owner; _layer = layer;
        var fresh = _handover.Attach(taken, now);
        if (fresh) VisibleAt = now;
        return fresh;
    }

    /// <summary>
    /// 從目前這一層拔下來。
    /// </summary>
    /// <param name="retire">
    /// 這一批結束了（到期、關閉、停用），不是交接。下一次出現要重新播入場動畫。
    /// </param>
    public void Detach(INotificationSurfaceOwner owner, DateTimeOffset now, bool retire)
    {
        if (!IsOwnedBy(owner)) return;
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

    /// <summary>把卡片從目前那一層移除，但不改交接狀態。</summary>
    private void Release()
    {
        if (_layer is not null && _card is not null) _layer.RemoveAdornment(_card);
        _layer = null; _owner = null;
    }

    /// <summary>編輯器自己把 adornment 收掉了（關閉、換版面）。</summary>
    private void OnLayerRemoved(INotificationSurfaceOwner owner)
    {
        if (!IsOwnedBy(owner)) return;
        _layer = null; _owner = null;
    }

    /// <remarks>例外由擁有者那幾個方法自己走 <c>SqlAssistPlatformGuard</c>；這裡只轉交。</remarks>
    private void Dispatch(Action<INotificationSurfaceOwner> action, RoutedEventArgs args)
    {
        if (_owner is not { } owner) return;
        action(owner);
        args.Handled = true;
    }
}
