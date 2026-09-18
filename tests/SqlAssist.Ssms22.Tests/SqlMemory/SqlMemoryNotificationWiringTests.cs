using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SqlAssist.Ssms22.Tests.SqlMemory;

/// <summary>
/// SQL Memory 的回饋通道接線：哪些事走通知卡片、哪些留在視窗、哪些仍要訊息框。
/// </summary>
/// <remarks>
/// 這幾個檔案要 SSMS 的殼層服務（<c>VsShellUtilities</c>、狀態列、工具窗框架）才跑得起來，
/// 不編進測試；判斷本身（可見度、合併、原因短語）各有純邏輯測試，這裡看的是接線。
/// </remarks>
public sealed class SqlMemoryNotificationWiringTests
{
    private const string CompactCommand = "Commands/SqlAssistSqlMemoryCompactCommand.cs";
    private const string SelfTestCommand = "Commands/SqlAssistSqlMemorySelfTestCommand.cs";
    private const string Host = "SqlMemory/SqlMemoryHost.cs";
    private const string UsagePanel = "SqlMemory/SqlMemoryUsagePanel.cs";
    private const string Browser = "SqlMemory/SqlMemoryBrowser.cs";
    private const string RevisionCommands = "SqlMemory/SqlFavoriteRevisionCommands.cs";
    private const string ToolWindow = "SqlMemory/SqlMemoryToolWindow.cs";
    private const string Actions = "SqlMemory/SqlMemoryActions.cs";
    private const string Dialogs = "UI/SqlAssistDialogs.cs";

    /// <summary>
    /// 整理成功只留在卡片上，失敗才跳訊息框。
    /// </summary>
    /// <remarks>
    /// 成功也跳訊息框的版本會在使用者早就回去編輯 SQL 之後搶走焦點，而那句話沒有任何
    /// 要決定的事；失敗相反，要讀完才知道是稍後再試還是先處理占用資料庫的程序。
    /// </remarks>
    [Fact]
    public void 整理成功不跳訊息框失敗才跳()
    {
        var source = ReadProductSource(CompactCommand);
        Assert.Contains("NotificationCatalog.CompactingSqlMemory", source, StringComparison.Ordinal);
        Assert.Contains("NotificationKind.SqlMemory, NotificationOrigin.User, NotificationLevel.Info", source, StringComparison.Ordinal);

        // 失敗說明是唯一送進訊息框的內容；成功那一條路上沒有訊息框。
        Assert.Single(Occurrences(source, "ShowMessageBox"));
        var show = Section(source, "if (failure is null) return;", "catch (OperationCanceledException)");
        Assert.Contains("OLEMSGICON_WARNING", show, StringComparison.Ordinal);
        Assert.Contains("notification.Fail();", Section(source, "catch (Exception error)", "return (error is"), StringComparison.Ordinal);
    }

    /// <summary>自我測試的報告是要讀的東西，成敗都留在訊息框；只有「還在跑」交給卡片。</summary>
    [Fact]
    public void 自我測試保留報告訊息框並以通知追蹤()
    {
        var source = ReadProductSource(SelfTestCommand);
        Assert.Contains("NotificationCatalog.TestingSqlMemoryStorage", source, StringComparison.Ordinal);
        Assert.Contains("notification.Fail();", source, StringComparison.Ordinal);
        Assert.Contains("notification.Cancel();", source, StringComparison.Ordinal);
        Assert.Single(Occurrences(source, "ShowMessageBox"));
    }

    /// <summary>
    /// 擷取被丟棄與容量警戒是事件：沒有執行期間，走 <c>Post</c>，三軸與狀態都明寫。
    /// </summary>
    /// <remarks>
    /// 丟棄是失敗——那一段 SQL 完全沒有記錄，值得進「通知失敗」回看；容量是降級——
    /// 資料都還在，只是要使用者處理，不該讓警示洗掉真正的失敗紀錄。
    /// </remarks>
    [Fact]
    public void 事件型通知帶著正確的三軸與狀態()
    {
        var source = ReadProductSource(Host);
        var dropped = Section(source, "private static void OnCaptureDropped", "private static void OnCapacityChanged");
        Assert.Contains("if (!drop.ShouldNotify) return;", dropped, StringComparison.Ordinal);
        Assert.Contains("NotificationCenter.Default.Post(NotificationCatalog.DroppingSqlCapture,", dropped, StringComparison.Ordinal);
        Assert.Contains("NotificationKind.SqlMemory, NotificationOrigin.Ambient, NotificationLevel.Notice,", dropped, StringComparison.Ordinal);
        Assert.Contains("NotificationStatus.Failed, message: drop.Reason);", dropped, StringComparison.Ordinal);

        var capacity = Section(source, "private static void OnCapacityChanged", "private static void OnMaintenanceFailed");
        Assert.Contains("if (!change.Notify) return;", capacity, StringComparison.Ordinal);
        Assert.Contains("NotificationCenter.Default.Post(NotificationCatalog.ExceedingSqlMemoryCapacity,", capacity, StringComparison.Ordinal);
        Assert.Contains("NotificationKind.SqlMemory, NotificationOrigin.Ambient, NotificationLevel.Notice,", capacity, StringComparison.Ordinal);
        Assert.Contains("NotificationStatus.Degraded, message: change.Reason);", capacity, StringComparison.Ordinal);

        var maintenance = Section(source, "private static void OnMaintenanceFailed", "private static async Task<ISqlMemoryStore>");
        Assert.Contains("NotificationCatalog.MaintainingSqlMemory", maintenance, StringComparison.Ordinal);
        Assert.Contains("NotificationKind.SqlMemory, NotificationOrigin.Ambient, NotificationLevel.Debug,", maintenance, StringComparison.Ordinal);
        Assert.Contains("NotificationStatus.Failed, message: reason);", maintenance, StringComparison.Ordinal);

        // 宿主不再需要套件：兩則事件都不經過狀態列。
        Assert.DoesNotContain("_package", source, StringComparison.Ordinal);
    }

    /// <summary>SQL Memory 不再寫 SSMS 狀態列；其他子系統（F12、區塊提示、結果格線）照舊。</summary>
    [Fact]
    public void SqlMemory不再使用狀態列()
    {
        foreach (var file in new[] { CompactCommand, SelfTestCommand, Host, UsagePanel, Browser, RevisionCommands })
            Assert.DoesNotContain("SqlAssistStatusBar", ReadProductSource(file), StringComparison.Ordinal);

        foreach (var file in ProductSources().Where(file => file.Contains(Path.DirectorySeparatorChar + "SqlMemory" + Path.DirectorySeparatorChar)))
            Assert.DoesNotContain("SqlAssistStatusBar", File.ReadAllText(file), StringComparison.Ordinal);
    }

    /// <summary>
    /// 等儲存的長操作一律以 <c>Begin</c> 追蹤，成功不再重寫一次視窗狀態列。
    /// </summary>
    /// <remarks>
    /// 卡片跟著作用中的宿主走：維護跑到一半切回編輯器或關掉工具窗，結果仍然看得到，
    /// 而視窗裡再寫一次「已完成」只是同一件事報兩遍。
    /// </remarks>
    [Fact]
    public void 長操作以通知追蹤且不重複寫視窗狀態()
    {
        var usage = ReadProductSource(UsagePanel);
        foreach (var title in new[]
                 {
                     "NotificationCatalog.MaintainingSqlMemory", "NotificationCatalog.ClearingSqlMemoryHistory",
                     "NotificationCatalog.CompactingSqlMemory", "NotificationCatalog.BackingUpSqlMemory",
                 })
            Assert.Contains(title, usage, StringComparison.Ordinal);

        var start = Section(usage, "private void Start(string title", "private static string Deleted");
        Assert.Contains("NotificationCenter.Default.Begin(title,", start, StringComparison.Ordinal);
        Assert.Contains("NotificationKind.SqlMemory, NotificationOrigin.User, NotificationLevel.Info);", start, StringComparison.Ordinal);
        // 失敗走卡片，不再經過 _report；狀態列只留備份位置這種卡片放不下的資訊。
        Assert.Contains("await _gate.RunAsync(_operation.Token, Failed, verb,", start, StringComparison.Ordinal);
        Assert.Contains("notification.Fail();", start, StringComparison.Ordinal);

        var browser = ReadProductSource(Browser);
        Assert.Contains("NotificationCatalog.RebuildingSqlMemory", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("正在備份並重新建立", browser, StringComparison.Ordinal);

        var revert = ReadProductSource(RevisionCommands);
        Assert.Contains("NotificationCatalog.RestoringFavoriteRevision", revert, StringComparison.Ordinal);
        Assert.DoesNotContain("已回溯：以", revert, StringComparison.Ordinal);
        // 衝突與「不確定有沒有成功」要當場讀完，留在視窗裡。
        Assert.Contains("report(\"收藏已被修改或移除，未回溯；已重新讀取版本清單。\");", revert, StringComparison.Ordinal);
        Assert.Contains("report(\"回溯未確認：\"", revert, StringComparison.Ordinal);
    }

    /// <summary>
    /// SQL Memory 的視窗都註冊成卡片宿主，而且只在兩個地方註冊。
    /// </summary>
    /// <remarks>
    /// 每個視窗各寫一次的版本，新增一個對話框就會忘記，而症狀是「卡片有時候不出現」——
    /// 沒有例外也沒有紀錄。工具窗要自己包 <c>AdornerDecorator</c>，否則卡片會掛到殼層主視窗。
    /// </remarks>
    [Fact]
    public void SqlMemory視窗集中註冊成通知宿主()
    {
        Assert.Contains("NotificationWindowHost.Register(window);", ReadProductSource(Dialogs), StringComparison.Ordinal);
        Assert.Contains("SqlAssistDialogs.Configure(window, title, width, height", ReadProductSource(Actions), StringComparison.Ordinal);

        var toolWindow = ReadProductSource(ToolWindow);
        Assert.Contains("new AdornerDecorator { Child = _host }", toolWindow, StringComparison.Ordinal);
        Assert.Contains("_notifications = NotificationWindowHost.Register(_host);", toolWindow, StringComparison.Ordinal);
        Assert.Contains("_notifications?.Dispose();", toolWindow, StringComparison.Ordinal);

        // 每個 SQL Memory 視窗都走集中的殼層設定，沒有人自己註冊一次。
        foreach (var window in new[]
                 {
                     "SqlMemory/FavoriteEditorWindow.cs", "SqlMemory/FavoriteRevisionsWindow.cs",
                     "SqlMemory/SqlMemoryCleanupWindow.cs",
                 })
        {
            var source = ReadProductSource(window);
            Assert.Contains("SqlMemoryActions.ConfigureWindow(this,", source, StringComparison.Ordinal);
            Assert.DoesNotContain("NotificationWindowHost", source, StringComparison.Ordinal);
        }
    }

    private static string Section(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.InRange(from, 0, int.MaxValue);
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.InRange(to, from, int.MaxValue);
        return source.Substring(from, to - from);
    }

    private static int[] Occurrences(string source, string value)
    {
        var found = new System.Collections.Generic.List<int>();
        for (var at = source.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            found.Add(at);
        return found.ToArray();
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
