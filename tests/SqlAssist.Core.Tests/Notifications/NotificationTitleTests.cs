using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Notifications;
using Xunit;

namespace SqlAssist.Core.Tests.Notifications;

/// <summary>
/// 已經入列的通知保存的是目錄那一句的身分而不是字串：換語言後重畫、成功後的過去式、
/// 合併與統計都跟著同一件事走。
/// </summary>
public sealed class NotificationTitleTests
{
    private static SqlLanguage English => SqlLanguage.Find("en")!;

    [Fact]
    public void 已顯示的通知換語言後用新語言重畫()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        var scope = center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata,
            NotificationOrigin.Typing, NotificationLevel.Info, subject: "dbo.Loan");
        now += TimeSpan.FromSeconds(2);
        scope.Dispose();
        var item = Assert.Single(Read(center));

        Assert.Equal("已載入欄位與定義（2.0 秒）", NotificationCatalog.Headline(item));
        using (SqlText.Use(English))
        {
            Assert.Equal("Loading columns and definitions", item.Title);
            Assert.Equal("Loaded columns and definitions (2.0s)", NotificationCatalog.Headline(item));
            Assert.Equal("Succeeded", NotificationCatalog.StatusText(item.Status));
        }
    }

    [Fact]
    public void 換語言前後的同一件事合併成一列也統計成一列()
    {
        var center = new NotificationCenter();
        Post(center);
        using (SqlText.Use(English))
        {
            Post(center);
            var merged = Assert.Single(NotificationMerge.Collapse(Read(center)));
            Assert.Equal(2, merged.Repeat);
            var entry = Assert.Single(center.Digest.Snapshot());
            Assert.Equal(2, entry.Count);
            Assert.Equal("Loading object list", entry.Title);
        }
    }

    [Fact]
    public void 提醒的標題訊息與按鈕換語言後重畫()
    {
        var center = new NotificationCenter();
        var item = center.Prompt(NotificationCatalog.UpdateAvailablePrompt("1.4.0", "https://example.invalid/1.4.0"),
            NotificationKind.Update, NotificationOrigin.Ambient, NotificationLevel.Notice)!;

        Assert.Equal("SqlAssist 有新版", item.Title);
        using (SqlText.Use(English))
        {
            Assert.Equal("SqlAssist update available", item.Title);
            Assert.StartsWith("1.4.0 is available.", item.Message, StringComparison.Ordinal);
            Assert.Equal(new[] { "Skip this version", "Download" }, item.Actions.Select(x => x.Label));
        }
    }

    [Fact]
    public void 目錄的標題在每個語言都認得回同一句()
    {
        var chinese = NotificationTitle.Resolve(NotificationCatalog.CopyingDefinition);
        using (SqlText.Use(English))
            Assert.Same(chinese, NotificationTitle.Resolve(NotificationCatalog.CopyingDefinition));
        Assert.Equal("複製定義", chinese.Key);
    }

    private static void Post(NotificationCenter center) =>
        center.Post(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing,
            NotificationLevel.Info, NotificationStatus.Succeeded);

    private static IReadOnlyList<NotificationItem> Read(NotificationCenter center) =>
        center.Snapshot(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
}
