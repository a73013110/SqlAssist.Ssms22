using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22.Notifications;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

/// <summary>通知島展開清單的列、抬頭與材質；形態與變形見 <see cref="NotificationIslandTests"/>。</summary>
public sealed class NotificationChromeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 狀態更新保留列身分且只有抬頭會轉()
    {
        WpfTest.Run(() =>
        {
            var center = new NotificationCenter();
            var task = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, source: "LibArchive");
            var island = new NotificationIsland();
            Expand(island, Content(center), motion: true);
            var row = Assert.IsType<NotificationRow>(island.Rows.Children[0]);
            Assert.Equal(NotificationVisualStatus.Running, row.Status);
            // 只有清單抬頭轉；列上的執行中是靜態光環。
            Assert.True(island.IsSpinning);
            Assert.False(row.Icon.IsSpinning);
            task.Fail(); task.Dispose();
            Expand(island, Content(center), motion: false);
            Assert.Same(row, island.Rows.Children[0]);
            Assert.Equal(NotificationVisualStatus.Failed, row.Status);
            Assert.False(island.IsSpinning);
            island.StopMotion();
        });
    }

    /// <summary>島嶼只認得自己的 view-model，換一個通知來源不必改版面。</summary>
    [Fact]
    public void 島嶼的介面不出現通知來源的型別()
    {
        foreach (var type in new[] { typeof(NotificationIsland), typeof(NotificationRow), typeof(NotificationPromptView) })
        {
            var signature = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType))
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType)))
                .Select(parameter => parameter.IsGenericType ? parameter.GetGenericArguments()[0] : parameter);
            Assert.DoesNotContain(signature, parameter => parameter.Namespace == typeof(NotificationItem).Namespace);
        }
    }

    [Fact]
    public void 以自訂記錄直接渲染不經過通知來源()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            var resources = new ThemeResourceSet();
            resources.Update(ThemePaletteTests.ColorsFor("light"));
            island.Resources.MergedDictionaries.Add(resources.Resources);
            var item = new NotificationActivityItem(7, "已還原預設片段", "SELECT 範本", "Loan.sql", "", "已寫回使用者資料夾",
                NotificationVisualStatus.Completed, "已完成", 1);
            Expand(island, new NotificationIslandContent(new[] { item }, "已還原預設片段", Array.Empty<NotificationPromptItem>()), motion: false);
            var row = Assert.IsType<NotificationRow>(island.Rows.Children[0]);
            Assert.Equal("已還原預設片段 · SELECT 範本", row.Headline);
            Assert.Equal("Loan.sql", island.DocumentLabel.Text);
            Assert.Equal("已寫回使用者資料夾", row.MessageText.Text);
            Assert.Equal(NotificationVisualStatus.Completed, row.Status);
            Assert.Equal(Visibility.Collapsed, row.Badge.Visibility);
            Assert.Equal(1, island.Progress.ScaleX);
            // 標題是正常前景、主旨淡色：同一行拆成兩段，讀出來的整句不變。
            var runs = row.TitleText.Inlines.OfType<System.Windows.Documents.Run>().ToArray();
            Assert.Equal(new[] { "已還原預設片段", " · SELECT 範本" }, runs.Select(run => run.Text));
            Assert.NotEqual(((SolidColorBrush)runs[0].Foreground).Color, ((SolidColorBrush)runs[1].Foreground).Color);
        });
    }

    [Fact]
    public void 合併後的重複次數顯示為徽章()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            var center = new NotificationCenter();
            for (var index = 0; index < 3; index++)
                using (center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata,
                           NotificationOrigin.Typing, NotificationLevel.Info, "dbo.Loan", "Loan.sql")) { }
            var content = Content(center);
            var merged = Assert.Single(content.Activities);
            Assert.Equal(3, merged.Repeat);
            Expand(island, content, motion: false);
            var row = Assert.IsType<NotificationRow>(Assert.Single(island.Rows.Children));
            var badge = row.Badge;
            Assert.Equal(Visibility.Visible, badge.Visibility);
            Assert.Equal("×3", ((TextBlock)badge.Child).Text);
            Assert.Contains("3 次", (string)badge.ToolTip);

            // 次數退回 1 時徽章要收掉，不能留著上一輪的數字；列的身分不變。
            Expand(island, content with { Activities = new[] { merged with { Repeat = 1 } } }, motion: false);
            Assert.Same(row, island.Rows.Children[0]);
            Assert.Equal(Visibility.Collapsed, badge.Visibility);
        });
    }

    [Fact]
    public void 共用文件只在抬頭顯示一次且訊息更新不重建列()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            var center = new NotificationCenter();
            using var first = center.Begin(NotificationCatalog.AnalyzingBlocks, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "SQLQuery1.sql");
            using var second = center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "SQLQuery1.sql");
            Expand(island, Content(center), motion: false);
            Assert.Equal("SQLQuery1.sql", island.DocumentLabel.Text);
            Assert.Equal(Visibility.Visible, island.DocumentLabel.Visibility);
            var row = Assert.IsType<NotificationRow>(island.Rows.Children[0]);
            Assert.Equal(Visibility.Collapsed, SourceLine(island, 0).Visibility);
            first.Report("正在建立區塊結構");
            Expand(island, Content(center), motion: false);
            Assert.Same(row, island.Rows.Children[0]);
            Assert.Equal("正在建立區塊結構", row.MessageText.Text);
            using var third = center.Begin(NotificationCatalog.LoadingIndexes, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "Loan.sql");
            Expand(island, Content(center), motion: false);
            Assert.Equal(Visibility.Collapsed, island.DocumentLabel.Visibility);
            Assert.Equal(Visibility.Visible, SourceLine(island, 0).Visibility);
        });
    }

    /// <summary>
    /// 抬頭那一行回答「這一批在哪一份文件上」，只看有文件的那幾列。
    /// </summary>
    /// <remarks>
    /// 文件與資料庫共用一格的版本，只要混進一列資料庫名或一列沒有出處的背景工作，
    /// 抬頭的檔名就整個收掉——同一次查詢看起來是檔名時有時無。
    /// </remarks>
    [Fact]
    public void 沒有文件的工作不收掉抬頭的檔名()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            var suggestions = new NotificationActivityItem(1, "已準備建議清單", "資料行", "SQLQuery1.sql", "", "",
                NotificationVisualStatus.Completed, "已完成", 1);
            var objects = new NotificationActivityItem(2, "已載入物件清單", "", "", "LibArchive", "",
                NotificationVisualStatus.Completed, "已完成", 1);
            var startup = new NotificationActivityItem(3, "已初始化 SqlAssist", "", "", "", "",
                NotificationVisualStatus.Completed, "已完成", 1);
            Expand(island, Activities(suggestions, objects, startup), motion: false);
            Assert.Equal("SQLQuery1.sql", island.DocumentLabel.Text);
            Assert.Equal(Visibility.Visible, island.DocumentLabel.Visibility);
            // 抬頭已經寫了文件，列上只留資料庫；兩者都沒有的那一列不預留空白行。
            Assert.Equal(Visibility.Collapsed, SourceLine(island, 0).Visibility);
            Assert.Equal("LibArchive", SourceLine(island, 1).Text);
            Assert.Equal(Visibility.Collapsed, SourceLine(island, 2).Visibility);

            // 指向兩份文件才收掉抬頭，並把文件補回各列。
            Expand(island, Activities(suggestions, objects with { Id = 4, Document = "Loan.sql" }), motion: false);
            Assert.Equal(Visibility.Collapsed, island.DocumentLabel.Visibility);
            Assert.Equal("SQLQuery1.sql", SourceLine(island, 0).Text);
            Assert.Equal("Loan.sql · LibArchive", SourceLine(island, 1).Text);
        });
    }

    /// <summary>浮層依最大外框固定視窗大小；超出的話島嶼的上緣會被視窗裁掉。</summary>
    [Fact]
    public void 最大的清單與疊起來的提醒都放得進浮層的固定外框()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            var center = new NotificationCenter();
            using var running = center.Begin(NotificationCatalog.AnalyzingBlocks, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info, document: "Lib_Reader.sql");
            for (var i = 0; i < 30; i++)
                using (center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata, NotificationOrigin.Typing, NotificationLevel.Info,
                           "dbo.Loan" + i, "Loan.sql", "LibArchive")) { }
            Expand(island, Content(center), motion: false);
            Assert.InRange(island.TargetSize.Height, 0, NotificationIsland.MaxExtent.Height);

            // 暫看活動時清單底部多一條回到提醒的附條，最高的就是這一種。
            var pending = new NotificationPromptItem(99, "SqlAssist 有新版", "1.4.0 已經發行。", NotificationPromptSeverity.Info,
                new[] { new NotificationPromptAction("a", "前往下載", true) }, 1, 1);
            var peeking = new NotificationIslandContent(Content(center).Activities, "", new[] { pending });
            var peek = new NotificationIslandState();
            peek.Update(new NotificationIslandInput(peeking.Activities.Count, peeking.Running, peeking.Failed, 1), Now);
            peek.TogglePeek(Now);
            island.Update(peeking, peek, motion: false);
            Assert.Equal(NotificationIslandShape.Expanded, island.Shape);
            Assert.Equal(Visibility.Visible, island.ListFooter.Visibility);
            Assert.InRange(island.TargetSize.Height, 0, NotificationIsland.MaxExtent.Height);

            var message = string.Concat(Enumerable.Repeat("SQL 只留在這台電腦。", 20));
            var prompts = Enumerable.Range(1, 3).Select(index => new NotificationPromptItem(index, "SQL Memory 超過容量警戒", message,
                NotificationPromptSeverity.Warning, new[] { new NotificationPromptAction("a", "開啟維護", true), new NotificationPromptAction("b", "略過", false) },
                index, 3)).ToArray();
            var stacked = new NotificationIslandContent(Content(center).Activities, "", prompts);
            var state = new NotificationIslandState();
            state.Update(new NotificationIslandInput(stacked.Activities.Count, stacked.Running, stacked.Failed, prompts.Length), Now);
            island.Update(stacked, state, motion: false);
            Assert.Equal(NotificationIslandShape.PromptStack, island.Shape);
            Assert.InRange(island.TargetSize.Width, 0, NotificationIsland.MaxExtent.Width);
            Assert.InRange(island.TargetSize.Height, 0, NotificationIsland.MaxExtent.Height);
        });
    }

    [Fact]
    public void 切換主題保留文字不透明且玻璃以兩百二十四覆蓋率起算()
    {
        WpfTest.Run(() =>
        {
            var island = new NotificationIsland();
            var resources = new ThemeResourceSet();
            island.Resources.MergedDictionaries.Add(resources.Resources);
            foreach (var theme in new[] { "mango", "plum", "high-contrast", "cool-breeze" })
            {
                var colors = ThemePaletteTests.ColorsFor(theme);
                resources.Update(colors);
                island.SetOptions(true, theme == "high-contrast");
                var expected = colors[ThemeBrush.ListBackground];
                if (theme != "high-contrast") expected.A = 224;
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(island.Surface.Background).Color);
                Assert.Equal(1, island.Surface.Opacity);
            }
        });
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, false, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, false, true, false, true)]
    public void 不受Windows動畫設定影響只覆寫系統偏好(bool enabled, bool ignoreWindows, bool system, bool contrast, bool expected)
    {
        Assert.Equal(expected, SqlAssistChrome.MotionPolicy(enabled, ignoreWindows, system, contrast));
    }

    internal static NotificationIslandContent Content(NotificationCenter center, SqlAssistSettings? settings = null) =>
        NotificationPresenter.ProjectIsland(center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue), settings ?? new SqlAssistSettings());

    /// <summary>以鍵盤焦點進來的狀態畫出展開清單；停駐與焦點的時序在狀態機的測試。</summary>
    internal static void Expand(NotificationIsland island, NotificationIslandContent content, bool motion)
    {
        var state = new NotificationIslandState();
        state.Update(new NotificationIslandInput(content.Activities.Count, content.Running, content.Failed, content.Prompts.Count), Now);
        state.FocusChanged(true, Now);
        island.Update(content, state, motion);
        island.Measure(new Size(NotificationIsland.MaxExtent.Width, NotificationIsland.MaxExtent.Height));
        island.Arrange(new Rect(island.DesiredSize));
        island.UpdateLayout();
    }

    private static NotificationIslandContent Activities(params NotificationActivityItem[] items) =>
        new(items, "", Array.Empty<NotificationPromptItem>());

    /// <summary>列上那一行出處：標題下方、訊息上方。</summary>
    private static TextBlock SourceLine(NotificationIsland island, int index) =>
        Assert.IsType<NotificationRow>(island.Rows.Children[index]).SourceLine;
}
