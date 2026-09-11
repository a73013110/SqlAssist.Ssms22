using System;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 通知卡片換編輯區時，這一次到底算不算「新出現在畫面上」。
/// </summary>
/// <remarks>
/// 只有新出現的那一次播入場動畫並重新起算最短可見時間。舊編輯區先把卡片拔下來、
/// 接手的那一個下一輪才掛上，中間隔的是一次派送——把那一段當成新出現，就是 F12 開新
/// 查詢視窗時提示消失又跳出來的那個閃爍。
///
/// 與 <see cref="NotificationSurface"/> 分開是為了測得到：那一份的 API 上有編輯器的
/// adornment 層，而這一段只有時間與旗標。
/// </remarks>
internal sealed class NotificationHandover
{
    /// <summary>
    /// 拔下來之後還算同一次交接的時間。
    /// </summary>
    /// <remarks>
    /// F12 要等新查詢視窗的文件顯示出來才輪得到刷新，因此放得比一次派送寬。放寬的代價
    /// 很小：真的結束的那一批走 <c>retire</c>，不吃這段寬限，下一批照樣有入場動畫。
    /// </remarks>
    internal static readonly TimeSpan Window = TimeSpan.FromMilliseconds(1200);

    private DateTimeOffset _detachedAt;
    private bool _pending;

    /// <summary>從編輯區拔下來。</summary>
    /// <param name="retire">這一批結束了（到期、關閉、停用），不是交接。</param>
    public void Detach(DateTimeOffset now, bool retire)
    {
        _detachedAt = now;
        _pending = !retire;
    }

    /// <summary>掛到編輯區上。</summary>
    /// <param name="attached">掛上去之前卡片還在別的編輯區上，是直接接手。</param>
    /// <returns>這一次是不是新出現在畫面上。</returns>
    public bool Attach(bool attached, DateTimeOffset now)
    {
        var handover = attached || (_pending && now - _detachedAt < Window);
        _pending = false;
        return !handover;
    }

    /// <summary>套件卸載：下一次一定是新出現。</summary>
    public void Reset() => _pending = false;
}
