using System;
using System.Collections.Generic;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22.Notifications;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Notifications;

public sealed class NotificationPresenterTests
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
        var item = Assert.Single(NotificationPresenter.Project(Read(center), new SqlAssistSettings()).Items);
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
        var presenter = new NotificationPresenter(center);
        var settings = new SqlAssistSettings();
        using var scope = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata,
            NotificationOrigin.Typing, NotificationLevel.Info);
        var first = presenter.Current(settings, retain: false);
        // 計時器每 100 ms 問一次；沒有新工作時不該每次重跑篩選、合併與投影。
        now += TimeSpan.FromMilliseconds(100);
        Assert.Same(first, presenter.Current(settings, retain: false));
        scope.Dispose();
        var second = presenter.Current(settings, retain: false);
        Assert.NotSame(first, second);
        Assert.Equal(NotificationVisualStatus.Completed, Assert.Single(second).Status);
    }

    /// <summary>關閉是全域的：提示同一時間只有一份，跟著作用中的宿主走。</summary>
    [Fact]
    public void 關閉只隱藏目前批次且新工作仍會出現()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        var presenter = new NotificationPresenter(center);
        var settings = new SqlAssistSettings();
        using (center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata,
                   NotificationOrigin.Typing, NotificationLevel.Info)) { }
        Assert.Single(presenter.Current(settings, retain: false));
        presenter.Dismiss(settings);
        Assert.Empty(presenter.Current(settings, retain: false));

        using (center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata,
                   NotificationOrigin.Typing, NotificationLevel.Info)) { }
        Assert.Single(presenter.Current(settings, retain: false));
    }

    [Fact]
    public void 展開狀態由呈現端保存且跟隨設定的預設值()
    {
        var center = new NotificationCenter();
        var presenter = new NotificationPresenter(center);
        var settings = new SqlAssistSettings();
        using var scope = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata,
            NotificationOrigin.Typing, NotificationLevel.Info);
        presenter.Current(settings, retain: false);
        Assert.True(presenter.Expanded);
        presenter.Toggle();
        Assert.False(presenter.Expanded);
        // 週期刷新不把自己按過的收合狀態蓋回預設。
        presenter.Current(settings, retain: false);
        Assert.False(presenter.Expanded);
        // 改了設定的預設值才跟著改。
        presenter.Current(new SqlAssistSettings { NotificationExpanded = false }, retain: false);
        Assert.False(presenter.Expanded);
        presenter.Current(new SqlAssistSettings(), retain: false);
        Assert.True(presenter.Expanded);
    }

    [Fact]
    public void 延遲顯示只看得見的工作()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        var presenter = new NotificationPresenter(center);
        var settings = new SqlAssistSettings();
        using var hidden = center.Begin(NotificationCatalog.PreparingSuggestions, NotificationKind.Completion,
            NotificationOrigin.Typing, NotificationLevel.Info);
        now += TimeSpan.FromMilliseconds(400);
        using var visible = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata,
            NotificationOrigin.Typing, NotificationLevel.Info);
        presenter.Current(settings, retain: false);
        // 隱藏的那一件已經超過門檻，但畫面上的工作剛開始，仍在延遲時間內。
        Assert.True(presenter.WithinDelay(TimeSpan.FromMilliseconds(300), now));
        now += TimeSpan.FromMilliseconds(400);
        Assert.False(presenter.WithinDelay(TimeSpan.FromMilliseconds(300), now));
    }

    private static IReadOnlyList<NotificationItem> Read(NotificationCenter center) =>
        center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue);
}
