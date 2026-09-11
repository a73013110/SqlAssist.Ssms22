using System;
using System.Collections.Generic;
using System.IO;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Editor;

public sealed class NotificationHostTests
{
    [Fact]
    public void 投影先篩可見度再合併並翻成卡片記錄()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        // 語法與區塊分析預設隱藏；合併鍵不含可見度，先併的話 ×N 會大於畫面上看過的次數。
        for (var index = 0; index < 2; index++)
            using (center.Begin(NotificationCatalog.AnalyzingBlocks, NotificationKind.Analysis,
                       NotificationOrigin.Typing, NotificationLevel.Info, "dbo.Loan", "Loan.sql")) { }
        for (var index = 0; index < 3; index++)
            using (center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata,
                       NotificationOrigin.Typing, NotificationLevel.Info, "dbo.Loan", "Loan.sql")) { }
        var item = Assert.Single(NotificationHost.Project(Read(center), new SqlAssistSettings()).Items);
        Assert.Equal("已載入欄位與定義", item.Title);
        Assert.Equal("dbo.Loan", item.Subject);
        Assert.Equal("Loan.sql", item.Document);
        Assert.Equal(NotificationVisualStatus.Completed, item.Status);
        Assert.Equal("已完成", item.StatusText);
        Assert.Equal(3, item.Repeat);
        Assert.Equal("", item.Message);
    }

    [Fact]
    public void 內容沒變不重新投影()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        var host = new NotificationHost(center);
        var settings = new SqlAssistSettings();
        using var scope = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata,
            NotificationOrigin.Typing, NotificationLevel.Info);
        var first = host.Current(settings, retain: false);
        // 計時器每 100 ms 問一次；沒有新工作時不該每次重跑篩選、合併與投影。
        now += TimeSpan.FromMilliseconds(100);
        Assert.Same(first, host.Current(settings, retain: false));
        scope.Dispose();
        var second = host.Current(settings, retain: false);
        Assert.NotSame(first, second);
        Assert.Equal(NotificationVisualStatus.Completed, Assert.Single(second).Status);
    }

    /// <summary>關閉是全域的：提示同一時間只有一份，跟著作用中的編輯區走。</summary>
    [Fact]
    public void 關閉只隱藏目前批次且新工作仍會出現()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        var host = new NotificationHost(center);
        var settings = new SqlAssistSettings();
        using (center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata,
                   NotificationOrigin.Typing, NotificationLevel.Info)) { }
        Assert.Single(host.Current(settings, retain: false));
        host.Dismiss(settings);
        Assert.Empty(host.Current(settings, retain: false));

        using (center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata,
                   NotificationOrigin.Typing, NotificationLevel.Info)) { }
        Assert.Single(host.Current(settings, retain: false));
    }

    [Fact]
    public void 展開狀態由呈現端保存且跟隨設定的預設值()
    {
        var center = new NotificationCenter();
        var host = new NotificationHost(center);
        var settings = new SqlAssistSettings();
        using var scope = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata,
            NotificationOrigin.Typing, NotificationLevel.Info);
        host.Current(settings, retain: false);
        Assert.True(host.Expanded);
        host.Toggle();
        Assert.False(host.Expanded);
        // 週期刷新不把自己按過的收合狀態蓋回預設。
        host.Current(settings, retain: false);
        Assert.False(host.Expanded);
        // 改了設定的預設值才跟著改。
        host.Current(new SqlAssistSettings { NotificationExpanded = false }, retain: false);
        Assert.False(host.Expanded);
        host.Current(new SqlAssistSettings(), retain: false);
        Assert.True(host.Expanded);
    }

    [Fact]
    public void 延遲顯示只看得見的工作()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        var host = new NotificationHost(center);
        var settings = new SqlAssistSettings();
        using var hidden = center.Begin(NotificationCatalog.PreparingSuggestions, NotificationKind.Completion,
            NotificationOrigin.Typing, NotificationLevel.Info);
        now += TimeSpan.FromMilliseconds(400);
        using var visible = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata,
            NotificationOrigin.Typing, NotificationLevel.Info);
        host.Current(settings, retain: false);
        // 隱藏的那一件已經超過門檻，但畫面上的工作剛開始，仍在延遲時間內。
        Assert.True(host.WithinDelay(TimeSpan.FromMilliseconds(300), now));
        now += TimeSpan.FromMilliseconds(400);
        Assert.False(host.WithinDelay(TimeSpan.FromMilliseconds(300), now));
    }

    /// <summary>
    /// 捲動只搬動提示，不進內容刷新。
    /// </summary>
    /// <remarks>
    /// Viewport 事件接到整套刷新的版本，每一次捲動都會進通知來源的鎖、跑到期清理與投影，
    /// 即使卡片根本沒掛上。這裡直接看接線：三個 viewport 事件都只能接到定位。
    /// </remarks>
    [Fact]
    public void 捲動與改變大小只接到定位()
    {
        var source = ReadProductSource("Editor/NotificationAdornment.cs");
        foreach (var notification in new[] { "ViewportWidthChanged", "ViewportHeightChanged", "ViewportLeftChanged" })
        {
            Assert.Contains($"view.{notification} += OnViewport;", source, StringComparison.Ordinal);
            Assert.Contains($"_view.{notification} -= OnViewport;", source, StringComparison.Ordinal);
        }

        var handler = Section(source, "private void OnViewport", "private void OnNotifications");
        Assert.Contains("PositionSurface", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Refresh", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("_refresh", handler, StringComparison.Ordinal);
    }

    /// <summary>非作用中的編輯區在事件處理常式就早退，不進 Refresh 才發現不是自己。</summary>
    [Fact]
    public void 非作用中的編輯區不排程內容刷新()
    {
        var source = ReadProductSource("Editor/NotificationAdornment.cs");
        var handler = Section(source, "private void OnNotifications", "private void OnActiveEditor");
        Assert.Contains("if (!_active) return;", handler, StringComparison.Ordinal);
        Assert.True(handler.IndexOf("if (!_active) return;", StringComparison.Ordinal) <
            handler.IndexOf("_refresh.Request", StringComparison.Ordinal));
        // 每個編輯區各自訂閱通知來源的版本已經改成訂閱呈現端。
        Assert.Contains("NotificationHost.Default.Changed += OnNotifications;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NotificationCenter.Default", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 卡片不歸編輯區持有，換編輯區也不算這一批結束。
    /// </summary>
    /// <remarks>
    /// 每個編輯區各建一張卡片的版本，F12 開新查詢視窗會讓同一份提示整個重建；
    /// 而把「不是我的了」當成結束，接手的那一個就會重播入場動畫。判斷本身在
    /// <c>NotificationHandover</c>，這裡看的是接線。
    /// </remarks>
    [Fact]
    public void 卡片不由編輯區持有且交接不算結束()
    {
        var source = ReadProductSource("Editor/NotificationAdornment.cs");
        Assert.DoesNotContain("CreateNotificationCard", source, StringComparison.Ordinal);
        Assert.Contains("NotificationSurface.Default", source, StringComparison.Ordinal);
        var refresh = Section(source, "private void Refresh()", "private void PositionSurface");
        Assert.Contains("Hide(now, retire: !enabled);", refresh, StringComparison.Ordinal);
    }

    private static string Section(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, StringComparison.Ordinal);
        Assert.InRange(from, 0, int.MaxValue);
        Assert.InRange(to, from, int.MaxValue);
        return source.Substring(from, to - from);
    }

    private static string ReadProductSource(string relativePath)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "SqlAssist.Ssms22", relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(relativePath);
    }

    private static IReadOnlyList<NotificationItem> Read(NotificationCenter center) =>
        center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue);
}
