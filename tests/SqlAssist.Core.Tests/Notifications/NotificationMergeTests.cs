using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Notifications;
using Xunit;

namespace SqlAssist.Core.Tests.Notifications;

public sealed class NotificationMergeTests
{
    [Fact]
    public void 種類標題主體文件與資料庫任一不同就不合併()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        Complete(center, NotificationCatalog.LoadingColumns, subject: "dbo.Loan", document: "Loan.sql", source: "LibArchive");
        Complete(center, NotificationCatalog.LoadingColumns, subject: "dbo.Loan", document: "Loan.sql", source: "LibArchive");
        Complete(center, NotificationCatalog.LoadingColumns, subject: "dbo.Copy", document: "Loan.sql", source: "LibArchive");
        Complete(center, NotificationCatalog.LoadingColumns, subject: "dbo.Loan", document: "Loan.sql", source: "LibReports");
        Complete(center, NotificationCatalog.LoadingColumns, subject: "dbo.Loan", document: "Report.sql", source: "LibArchive");
        Complete(center, NotificationCatalog.LoadingIndexes, subject: "dbo.Loan", document: "Loan.sql", source: "LibArchive");
        Complete(center, NotificationCatalog.LoadingColumns, kind: NotificationKind.Preview,
            subject: "dbo.Loan", document: "Loan.sql", source: "LibArchive");

        var merged = NotificationMerge.Collapse(Read(center));
        Assert.Equal(6, merged.Count);
        Assert.Equal(2, merged[0].Repeat);
        Assert.All(merged.Skip(1), item => Assert.Equal(1, item.Repeat));
    }

    [Fact]
    public void 執行中的工作各自成列()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        var first = Begin(center, NotificationCatalog.LoadingObjects);
        var second = Begin(center, NotificationCatalog.LoadingObjects);

        var running = NotificationMerge.Collapse(Read(center));
        Assert.Equal(2, running.Count);
        Assert.All(running, item => Assert.Equal(1, item.Repeat));

        first.Dispose();
        second.Dispose();
        var done = Assert.Single(NotificationMerge.Collapse(Read(center)));
        Assert.Equal(2, done.Repeat);
    }

    [Fact]
    public void 失敗與降級不併入成功()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        Complete(center, NotificationCatalog.LoadingColumns);
        Complete(center, NotificationCatalog.LoadingColumns, scope => scope.Fail());
        Complete(center, NotificationCatalog.LoadingColumns, scope => scope.Degrade());
        Complete(center, NotificationCatalog.LoadingColumns, scope => scope.Fail());

        var merged = NotificationMerge.Collapse(Read(center));
        Assert.Equal(3, merged.Count);
        Assert.Equal(NotificationStatus.Succeeded, merged[0].Status);
        Assert.Equal(1, merged[0].Repeat);
        Assert.Equal(NotificationStatus.Failed, merged[1].Status);
        Assert.Equal(2, merged[1].Repeat);
        Assert.Equal(NotificationStatus.Degraded, merged[2].Status);
        Assert.Equal(1, merged[2].Repeat);
    }

    [Fact]
    public void 代表列沿用最早那一項的身分與位置()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        Complete(center, NotificationCatalog.LoadingObjects);
        Complete(center, NotificationCatalog.LoadingIndexes);
        var first = Read(center)[0];
        Complete(center, NotificationCatalog.LoadingObjects);
        Complete(center, NotificationCatalog.LoadingObjects);

        var merged = NotificationMerge.Collapse(Read(center));
        Assert.Equal(2, merged.Count);
        Assert.Equal(first.Id, merged[0].Id);
        Assert.Equal(first.Started, merged[0].Started);
        Assert.Equal(3, merged[0].Repeat);
        Assert.Equal(NotificationCatalog.LoadingIndexes, merged[1].Title);
    }

    // 次數只在 Repeat 上；敘述不重複寫一次，畫面上的 ×N 是卡片的徽章。
    [Fact]
    public void 重複次數只進計數不進敘述()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        Complete(center, NotificationCatalog.LoadingColumns, subject: "dbo.Loan");
        var single = Assert.Single(NotificationMerge.Collapse(Read(center)));
        Assert.Equal("已載入欄位與定義", NotificationCatalog.Headline(single));
        Assert.Equal(1, single.Repeat);

        Complete(center, NotificationCatalog.LoadingColumns, subject: "dbo.Loan");
        var merged = Assert.Single(NotificationMerge.Collapse(Read(center)));
        Assert.Equal("已載入欄位與定義", NotificationCatalog.Headline(merged));
        Assert.Equal("dbo.Loan", merged.Subject);
        Assert.Equal(2, merged.Repeat);
    }

    private static NotificationScope Begin(NotificationCenter center, string title,
        NotificationKind kind = NotificationKind.Metadata, string subject = "",
        string document = "", string source = "") =>
        center.BeginDetached(title, kind, NotificationOrigin.Typing, NotificationLevel.Info, subject, document, source);

    private static void Complete(NotificationCenter center, string title, Action<NotificationScope>? result = null,
        NotificationKind kind = NotificationKind.Metadata, string subject = "",
        string document = "", string source = "")
    {
        using var scope = Begin(center, title, kind, subject, document, source);
        result?.Invoke(scope);
    }

    private static IReadOnlyList<NotificationItem> Read(NotificationCenter center) =>
        center.Snapshot(TimeSpan.FromMilliseconds(2500), TimeSpan.FromSeconds(6));
}
