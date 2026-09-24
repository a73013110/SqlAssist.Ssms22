using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Notifications;

/// <summary>
/// 控制器、浮層、派送與提醒來源的接線。
/// </summary>
/// <remarks>
/// 這幾個檔案依賴 SSMS 的殼層、設定與主題服務，或是真的 Win32 視窗，不編進測試；
/// 這裡直接看原始碼的結構。判斷本身（形態、期限、定位、可見度）各有抽出的純邏輯測試。
/// </remarks>
public sealed class NotificationIslandControllerTests
{
    private const string Controller = "Notifications/NotificationIslandController.cs";
    private const string Overlay = "Notifications/NotificationOverlay.cs";
    private const string Router = "Notifications/NotificationActionRouter.cs";
    private const string Package = "SqlAssistPackage.cs";
    private const string UpdateCheck = "Commands/SqlAssistUpdateCheckCommand.cs";

    /// <summary>整個處理程序只有一個計時器與一份全域訂閱，而且在套件初始化時就接上。</summary>
    /// <remarks>
    /// 等第一個編輯區出現才接上的版本，啟動時檢查到的新版本會因為還沒有地方畫而被吃掉；
    /// 每個視窗各持有一份的版本，N 個視窗就是 N 個 100 ms 計時器。
    /// </remarks>
    [Fact]
    public void 控制器是唯一的計時器與全域訂閱且套件初始化就接上()
    {
        var controller = ReadProductSource(Controller);
        Assert.Single(Regex.Matches(controller, @"new DispatcherTimer\(").Cast<Match>());
        var start = Section(controller, "public void Start(", "public void Shutdown(");
        foreach (var subscription in new[]
                 {
                     "NotificationPresenter.Default.Changed += OnNotifications;",
                     "SsmsWindows.FocusMoved += OnFocusMoved;",
                     "SqlAssistSettingsStore.Changed += OnSettings;",
                     "VsThemeBrushes.Changed += OnTheme;",
                 })
            Assert.Contains(subscription, start, StringComparison.Ordinal);

        var overlay = ReadProductSource(Overlay);
        foreach (var forbidden in new[] { "DispatcherTimer(", "ThemeRefreshQueue", "NotificationPresenter", "SqlAssistSettingsStore" })
            Assert.DoesNotContain(forbidden, overlay, StringComparison.Ordinal);

        foreach (var file in ProductSources().Where(file => !file.EndsWith("NotificationIslandController.cs", StringComparison.Ordinal)))
            Assert.DoesNotContain("NotificationPresenter.Default.Changed +=", File.ReadAllText(file), StringComparison.Ordinal);

        var package = ReadProductSource(Package);
        Assert.Contains("NotificationIslandController.Default.Start(", package, StringComparison.Ordinal);
        Assert.Contains("NotificationIslandController.Default.Shutdown", package, StringComparison.Ordinal);
        // 控制器先接上，啟動時的更新檢查才送得出提醒。
        Assert.True(package.IndexOf("NotificationIslandController.Default.Start(", StringComparison.Ordinal) <
                    package.IndexOf("SqlAssistUpdateCheckCommand.ScheduleStartupCheck(", StringComparison.Ordinal));
    }

    /// <summary>浮層沒有在畫面上時，主題與焦點的事件不排程刷新；通知與設定一律排程，新內容才出得來。</summary>
    [Fact]
    public void 浮層不在畫面上時主題與焦點不排程刷新()
    {
        var controller = ReadProductSource(Controller);
        foreach (var (start, end) in new[]
                 {
                     ("private void OnFocusMoved", "private void OnSettings"),
                     ("private void OnTheme", "private void OnTick"),
                 })
            AssertReturnsBefore(Section(controller, start, end), "if (!_engaged) return;", "_refresh");

        // 擁有者最小化時不問通知來源：一問就會跑到期清理，把還沒看到的結果收掉。
        var refresh = Section(controller, "private void Refresh()", "private void Render(");
        Assert.True(refresh.IndexOf("Suspend();", StringComparison.Ordinal) < refresh.IndexOf("presenter.Island(", StringComparison.Ordinal));
        // 內容與形態都沒變時不重畫。
        Assert.Contains("if (!force && !changed && ReferenceEquals(content, _rendered)) return;",
            Section(controller, "private void Render(", "private void Retire("), StringComparison.Ordinal);
    }

    /// <summary>錨點跟著使用者正在操作的框架，不跟著最後取得焦點的 SQL 編輯區。</summary>
    /// <remarks>
    /// 跟著編輯區的版本：查詢視窗拆出去之後回主視窗操作 SQL Search，通知出現在拆出去的那個視窗上，
    /// 被主視窗蓋住或在另一台螢幕時就像沒出現；那個視窗最小化時則整個停住。
    /// </remarks>
    [Fact]
    public void 錨點跟著作用中框架而不是編輯區()
    {
        var controller = ReadProductSource(Controller);
        Assert.DoesNotContain("ActiveSqlEditor", controller, StringComparison.Ordinal);
        Assert.Contains("NotificationAnchor.Choose(SsmsWindows.ActiveFrame, _overlay?.Anchor, SsmsWindows.Main, SsmsWindows.IsShowing)",
            Section(controller, "private void Refresh()", "private void Render("), StringComparison.Ordinal);

        // 框架用白名單認：排除對話框的黑名單漏掉了 SSMS 連線對話框自己那一套 DialogWindow。
        var windows = ReadProductSource("UI/SsmsWindows.cs");
        Assert.Contains("public static bool IsFrame(Window window) => window is FloatingWindow || ReferenceEquals(window, Main);",
            windows, StringComparison.Ordinal);

        // 對話框的擁有者只有一個出處。
        foreach (var file in ProductSources().Where(file => !file.EndsWith("SsmsWindows.cs", StringComparison.Ordinal)))
            Assert.DoesNotMatch(@"Window\.GetWindow\([^)]*\)\s*\?\?\s*Application\.Current", File.ReadAllText(file));
    }

    /// <summary>
    /// 附屬、不啟用、不置頂、不進工作列與 Alt+Tab，島嶼以外的點擊穿透。
    /// </summary>
    /// <remarks>
    /// 少了其中任何一項都沒有例外：置頂的版本蓋在別的應用程式上，沒有 <c>WS_EX_NOACTIVATE</c>
    /// 的版本一點就搶走查詢視窗的游標，沒有 <c>HTTRANSPARENT</c> 的版本柔影那一圈點不到底下的格線。
    /// </remarks>
    [Fact]
    public void 浮層是不啟用的透明附屬視窗且島嶼以外穿透()
    {
        var overlay = ReadProductSource(Overlay);
        foreach (var required in new[]
                 {
                     "WindowStyle = WindowStyle.None;", "AllowsTransparency = true;", "ShowActivated = false;",
                     "ShowInTaskbar = false;", "Topmost = false;", "ResizeMode = ResizeMode.NoResize;",
                     "Native.WsExNoActivate | Native.WsExToolWindow", "Native.HtTransparent", "Island.InputHitTest(point)",
                     "Owner = anchor;", "Native.SwpNoActivate",
                 })
            Assert.Contains(required, overlay, StringComparison.Ordinal);

        // 視窗大小固定；變形只在視窗裡面發生，閒置時整個隱藏。
        Assert.Contains("Island.Vanished += (_, _) => Hide();", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetSize", overlay, StringComparison.Ordinal);
        // 擁有者關閉前先放手，附屬視窗才不會跟著被關掉。
        Assert.Contains("anchor.Closing += OnAnchorClosing;", overlay, StringComparison.Ordinal);
        // 不在鍵盤模式時擋下焦點，點按鈕不會把浮層啟用。
        AssertReturnsBefore(Section(overlay, "private void OnPreviewGotKeyboardFocus", "private void OnPreviewKeyDown"),
            "if (!_keyboard)", "args.Handled = true;");
    }

    /// <summary>按鈕先收掉那一則再派送，處理常式包在 Guard 裡。</summary>
    [Fact]
    public void 按鈕先收掉提醒再派送()
    {
        var router = ReadProductSource(Router);
        var invoke = Section(router, "public static void Invoke(", "\n}");
        AssertReturnsBefore(invoke, "TryResolve(promptId, actionId, out var action)", "handler(action.Argument)");
        Assert.Contains("SqlAssistPlatformGuard.Run(", invoke, StringComparison.Ordinal);

        var package = ReadProductSource(Package);
        foreach (var id in new[] { "UpdateSkip", "UpdateDownload", "SqlMemoryOpenMaintenance", "SqlMemoryOpen" })
            Assert.Contains($"NotificationActionRouter.Register(NotificationActionIds.{id},", package, StringComparison.Ordinal);
    }

    /// <summary>
    /// 有新版一律跳出提醒，不再自動開瀏覽器；自動檢查尊重略過的版本，手動檢查不理會略過與「稍後」。
    /// </summary>
    [Fact]
    public void 更新檢查以提醒回報新版()
    {
        var source = ReadProductSource(UpdateCheck);
        Assert.DoesNotContain("OpenReleasePage()", source, StringComparison.Ordinal);
        var automatic = Section(source, "private static void AnnounceAutomatic(", "private static void Prompt(");
        Assert.Contains("SqlAssistState.SkippedUpdateTag", automatic, StringComparison.Ordinal);
        Assert.Contains("NotificationOrigin.Ambient", automatic, StringComparison.Ordinal);
        var manual = Section(source, "private static async Task ExecuteAsync(", "/// <summary>\n    /// 記下這一次");
        Assert.Contains("Prompt(result, NotificationOrigin.User, NotificationLevel.Info);", manual, StringComparison.Ordinal);
        Assert.DoesNotContain("SkippedUpdateTag", manual, StringComparison.Ordinal);
        // 今天問過了就用快取比對，不連網也照樣提醒。
        var startup = Section(source, "public static void ScheduleStartupCheck(", "public static void SkipVersion(");
        AssertReturnsBefore(startup, "AnnounceAutomatic(SqlAssistUpdateCheck.Compare(SqlAssistPackage.PackageVersion, SqlAssistState.UpdateTag));", "CheckAsync(");
    }

    /// <summary>宿主抽象與各視窗的註冊整組移除；方案裡沒有任何一處還認得它們。</summary>
    [Fact]
    public void 舊的宿主機制已經整組移除()
    {
        var removed = new[]
        {
            "INotificationSurfaceHost", "NotificationEditorHost", "NotificationWindowHost", "NotificationHostPriority",
            "NotificationHandover", "NotificationSurfaceController", "NotificationCard.xaml", "CreateNotificationCard",
        };
        foreach (var file in ProductSources().Concat(TestSources()))
        {
            if (file.EndsWith(nameof(NotificationIslandControllerTests) + ".cs", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            foreach (var name in removed) Assert.DoesNotContain(name, text, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"\bNotificationSurface\b|\bNotificationCard", text);
        }

        foreach (var window in new[] { "SqlMemory/SqlMemoryToolWindow.cs", "Search/SqlSearchToolWindow.cs" })
            Assert.DoesNotContain("AdornerDecorator", ReadProductSource(window), StringComparison.Ordinal);
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

    private static string[] ProductSources() => Sources(ProductRoot());

    private static string[] TestSources() => Sources(Path.Combine(RepositoryRoot(), "tests"));

    private static string[] Sources(string root) =>
        Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".csproj", StringComparison.Ordinal) ||
                           file.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                           !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToArray();

    private static string ProductRoot() => Path.Combine(RepositoryRoot(), "src", "SqlAssist.Ssms22");

    private static string RepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, "src", "SqlAssist.Ssms22"))) return current.FullName;
        throw new DirectoryNotFoundException("src/SqlAssist.Ssms22");
    }
}
