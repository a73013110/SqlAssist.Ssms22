using System;
using System.Windows;
using System.Windows.Threading;
using SqlAssist.Ssms22.Notifications;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Notifications;

/// <summary>卡片該掛在哪一個宿主上，以及換宿主時是不是重播入場。</summary>
public sealed class NotificationHostPriorityTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 有焦點的編輯區優先於有焦點的視窗()
    {
        var editor = new FakeHost(NotificationSurfaceHostKind.Editor, NotificationSurfaceHostActivity.Focused);
        var window = new FakeHost(NotificationSurfaceHostKind.Window, NotificationSurfaceHostActivity.Focused);
        Assert.Same(editor, NotificationHostPriority.Select(new[] { window, editor }, owner: null));
    }

    /// <summary>最後用過的編輯區幾乎總是看得見；排在視窗前面的話 SQL Memory 永遠搶不到卡片。</summary>
    [Fact]
    public void 有焦點的視窗優先於最後用過的編輯區()
    {
        var editor = new FakeHost(NotificationSurfaceHostKind.Editor, NotificationSurfaceHostActivity.Recent);
        var window = new FakeHost(NotificationSurfaceHostKind.Window, NotificationSurfaceHostActivity.Focused);
        Assert.Same(window, NotificationHostPriority.Select(new[] { editor, window }, owner: editor));
    }

    /// <summary>焦點移到物件總管或別的應用程式時，提示留在最後用過的編輯區。</summary>
    [Fact]
    public void 沒有焦點時留在最後用過的編輯區()
    {
        var editor = new FakeHost(NotificationSurfaceHostKind.Editor, NotificationSurfaceHostActivity.Recent);
        var window = new FakeHost(NotificationSurfaceHostKind.Window, NotificationSurfaceHostActivity.Inactive);
        Assert.Same(editor, NotificationHostPriority.Select(new[] { window, editor }, owner: window));
    }

    [Fact]
    public void 看不見的宿主不接卡片()
    {
        var editor = new FakeHost(NotificationSurfaceHostKind.Editor, NotificationSurfaceHostActivity.Recent);
        var window = new FakeHost(NotificationSurfaceHostKind.Window, NotificationSurfaceHostActivity.Focused) { IsVisible = false };
        Assert.Same(editor, NotificationHostPriority.Select(new[] { window, editor }, owner: null));
    }

    /// <summary>沒有可掛的宿主就不顯示，不退回 SSMS 主視窗。</summary>
    [Fact]
    public void 都沒有就不顯示()
    {
        var editor = new FakeHost(NotificationSurfaceHostKind.Editor, NotificationSurfaceHostActivity.Recent) { IsVisible = false };
        var window = new FakeHost(NotificationSurfaceHostKind.Window, NotificationSurfaceHostActivity.Inactive);
        Assert.Null(NotificationHostPriority.Select(new[] { editor, window }, owner: editor));
        Assert.Null(NotificationHostPriority.Select(Array.Empty<INotificationSurfaceHost>(), owner: null));
    }

    /// <summary>非作用中的視窗宿主不必往上找圖層。</summary>
    [Fact]
    public void 非作用中的宿主不讀可見度()
    {
        var window = new FakeHost(NotificationSurfaceHostKind.Window, NotificationSurfaceHostActivity.Inactive);
        NotificationHostPriority.Select(new[] { window }, owner: null);
        Assert.Equal(0, window.VisibilityReads);
    }

    [Fact]
    public void 同級時留在目前的擁有者上()
    {
        var first = new FakeHost(NotificationSurfaceHostKind.Window, NotificationSurfaceHostActivity.Focused);
        var second = new FakeHost(NotificationSurfaceHostKind.Window, NotificationSurfaceHostActivity.Focused);
        Assert.Same(first, NotificationHostPriority.Select(new[] { first, second }, owner: null));
        Assert.Same(second, NotificationHostPriority.Select(new[] { first, second }, owner: second));
    }

    /// <summary>
    /// SQL 分頁與 SQL Memory 工具窗互切、關閉工具窗都是交接，不重播入場。
    /// </summary>
    /// <remarks>控制器依選擇結果呼叫表面；表面把「直接接手」與「拔下後接手」交給交接判斷。</remarks>
    [Fact]
    public void 編輯區與工具窗互切不算新出現()
    {
        var handover = new NotificationHandover();
        var editor = new FakeHost(NotificationSurfaceHostKind.Editor, NotificationSurfaceHostActivity.Focused);
        var window = new FakeHost(NotificationSurfaceHostKind.Window, NotificationSurfaceHostActivity.Inactive);
        var hosts = new INotificationSurfaceHost[] { editor, window };
        Assert.Same(editor, NotificationHostPriority.Select(hosts, owner: null));
        Assert.True(handover.Attach(attached: false, Origin));

        // 點進工具窗：編輯區退成最後用過的，卡片直接從編輯區搬過去。
        editor.Activity = NotificationSurfaceHostActivity.Recent;
        window.Activity = NotificationSurfaceHostActivity.Focused;
        Assert.Same(window, NotificationHostPriority.Select(hosts, owner: editor));
        Assert.False(handover.Attach(attached: true, Origin + TimeSpan.FromMilliseconds(100)));

        // 關閉工具窗：先拔下，下一輪才由編輯區接手。
        handover.Detach(Origin + TimeSpan.FromMilliseconds(500), retire: false);
        Assert.Same(editor, NotificationHostPriority.Select(new INotificationSurfaceHost[] { editor }, owner: null));
        Assert.False(handover.Attach(attached: false, Origin + TimeSpan.FromMilliseconds(600)));
    }

    private sealed class FakeHost : INotificationSurfaceHost
    {
        private bool _visible = true;

        public FakeHost(NotificationSurfaceHostKind kind, NotificationSurfaceHostActivity activity)
        { Kind = kind; Activity = activity; }

        public event EventHandler? ViewportChanged { add { } remove { } }
        public event EventHandler? StateChanged { add { } remove { } }

        public NotificationSurfaceHostKind Kind { get; }
        public NotificationSurfaceHostActivity Activity { get; set; }
        public int VisibilityReads { get; private set; }

        public bool IsVisible
        {
            get { VisibilityReads++; return _visible; }
            set => _visible = value;
        }

        public Dispatcher Dispatcher => Dispatcher.CurrentDispatcher;
        public Rect Viewport => new(0, 0, 800, 600);
        public bool Mount(NotificationCard card, Action removed) => true;
        public void Unmount(NotificationCard card) { }
        public void Dispose() { }
    }
}
