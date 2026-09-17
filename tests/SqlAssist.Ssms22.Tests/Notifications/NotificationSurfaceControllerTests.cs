using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Notifications;

/// <summary>
/// 控制器與宿主之間的接線。
/// </summary>
/// <remarks>
/// 控制器與宿主依賴 SSMS 編輯器與殼層的設定、主題服務，不編進測試；這裡直接看原始碼的
/// 結構。判斷本身（優先序、期限、交接）各有抽出的純邏輯測試。
/// </remarks>
public sealed class NotificationSurfaceControllerTests
{
    private const string Controller = "Notifications/NotificationSurfaceController.cs";
    private const string EditorHost = "Editor/NotificationEditorHost.cs";
    private const string WindowHost = "Notifications/NotificationWindowHost.cs";

    /// <summary>
    /// 整個處理程序只有一個計時器與一份全域訂閱。
    /// </summary>
    /// <remarks>
    /// 每個編輯區各持有一份的版本，N 個編輯區就是 N 個 100 ms 計時器與 N 份通知、設定、
    /// 主題與作用中編輯區的訂閱，而每一份都要自己維護早退旗標。
    /// </remarks>
    [Fact]
    public void 控制器是唯一的計時器與全域訂閱()
    {
        var controller = ReadProductSource(Controller);
        Assert.Single(Regex.Matches(controller, @"new DispatcherTimer\(").Cast<Match>());
        foreach (var subscription in new[]
                 {
                     "NotificationPresenter.Default.Changed += OnNotifications;",
                     "ActiveSqlEditor.Changed += OnActiveEditor;",
                     "SqlAssistSettingsStore.Changed += OnSettings;",
                     "VsThemeBrushes.Changed += OnTheme;",
                 })
        {
            Assert.Contains(subscription, controller, StringComparison.Ordinal);
            // 只在第一次註冊宿主時接上一次，不在每一次註冊時重複。
            Assert.Contains(subscription, Section(controller, "private void Start(", "private void OnViewport"), StringComparison.Ordinal);
        }

        foreach (var host in new[] { EditorHost, WindowHost })
        {
            var source = ReadProductSource(host);
            foreach (var forbidden in new[]
                     {
                         "DispatcherTimer(", "ThemeRefreshQueue", "NotificationPresenter.Default.Changed +=",
                         "ActiveSqlEditor.Changed +=", "SqlAssistSettingsStore.Changed +=", "VsThemeBrushes.Changed +=",
                     })
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }

        foreach (var file in ProductSources().Where(file => !file.EndsWith("NotificationSurfaceController.cs", StringComparison.Ordinal)))
            Assert.DoesNotContain("NotificationPresenter.Default.Changed +=", File.ReadAllText(file), StringComparison.Ordinal);
    }

    /// <summary>
    /// 捲動與改變大小只搬動提示，不進內容刷新。
    /// </summary>
    /// <remarks>
    /// 接到整套刷新的版本，每一次捲動都會進通知來源的鎖、跑到期清理與投影，即使卡片根本
    /// 沒掛在那裡。
    /// </remarks>
    [Fact]
    public void 捲動與改變大小只接到定位()
    {
        var editor = ReadProductSource(EditorHost);
        foreach (var notification in new[] { "ViewportWidthChanged", "ViewportHeightChanged", "ViewportLeftChanged" })
        {
            Assert.Contains($"view.{notification} += OnViewport;", editor, StringComparison.Ordinal);
            Assert.Contains($"_view.{notification} -= OnViewport;", editor, StringComparison.Ordinal);
        }

        var editorHandler = Section(editor, "private void OnViewport", "private void OnState(");
        Assert.Contains("ViewportChanged?.Invoke", editorHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("StateChanged", editorHandler, StringComparison.Ordinal);

        var window = ReadProductSource(WindowHost);
        Assert.Contains("element.SizeChanged += OnSize;", window, StringComparison.Ordinal);
        var windowHandler = Section(window, "private void OnSize", "private void OnState(");
        Assert.Contains("ViewportChanged?.Invoke", windowHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("StateChanged", windowHandler, StringComparison.Ordinal);

        var controller = ReadProductSource(Controller);
        Assert.Contains("host.ViewportChanged += OnViewport;", controller, StringComparison.Ordinal);
        var handler = Section(controller, "private void OnViewport", "private void OnHostState");
        Assert.Contains("_surface.IsOwnedBy(host)", handler, StringComparison.Ordinal);
        Assert.Contains("_surface.Place()", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Refresh", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("_refresh", handler, StringComparison.Ordinal);
    }

    /// <summary>沒有宿主可掛、或宿主非作用中時，在事件處理常式就早退，不排程一輪刷新才發現。</summary>
    [Fact]
    public void 非作用中不排程內容刷新()
    {
        var controller = ReadProductSource(Controller);
        foreach (var (start, end) in new[]
                 {
                     ("private void OnNotifications", "private void OnActiveEditor"),
                     ("private void OnSettings", "private void OnTheme"),
                     ("private void OnTheme", "private void OnTick"),
                 })
            AssertReturnsBefore(Section(controller, start, end), "if (!_engaged) return;", "_refresh");

        AssertReturnsBefore(Section(controller, "private void OnHostState", "private void OnNotifications"),
            "host.Activity == NotificationSurfaceHostActivity.Inactive)) return;", "_refresh");
    }

    /// <summary>
    /// 卡片不歸宿主持有，換宿主也不算這一批結束。
    /// </summary>
    /// <remarks>
    /// 每個宿主各建一張卡片的版本，F12 開新查詢視窗會讓同一份提示整個重建；
    /// 而把「宿主關了」或「暫時沒有宿主」當成結束，接手的那一個就會重播入場動畫。
    /// 判斷本身在 <c>NotificationHandover</c>，這裡看的是接線。
    /// </remarks>
    [Fact]
    public void 卡片不由宿主持有且交接不算結束()
    {
        foreach (var host in new[] { EditorHost, WindowHost })
            Assert.DoesNotContain("CreateNotificationCard", ReadProductSource(host), StringComparison.Ordinal);

        var controller = ReadProductSource(Controller);
        Assert.Contains("Hide(DateTimeOffset.UtcNow, retire: false);",
            Section(controller, "public void Unregister", "public void Shutdown"), StringComparison.Ordinal);
        Assert.Contains("Hide(now, retire: !enabled);",
            Section(controller, "private void Refresh()", "private void OnDetailsToggled"), StringComparison.Ordinal);
    }

    private static void AssertReturnsBefore(string handler, string guard, string work)
    {
        var at = handler.IndexOf(guard, StringComparison.Ordinal);
        Assert.InRange(at, 0, int.MaxValue);
        Assert.True(at < handler.IndexOf(work, StringComparison.Ordinal));
    }

    private static string Section(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.InRange(from, 0, int.MaxValue);
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.InRange(to, from, int.MaxValue);
        return source.Substring(from, to - from);
    }

    private static string ReadProductSource(string relativePath) =>
        File.ReadAllText(Path.Combine(ProductRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string[] ProductSources() =>
        Directory.GetFiles(ProductRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                           !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToArray();

    private static string ProductRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "SqlAssist.Ssms22");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("src/SqlAssist.Ssms22");
    }
}
