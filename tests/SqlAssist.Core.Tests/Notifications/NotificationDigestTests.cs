using System;
using System.Linq;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Notifications;

public sealed class NotificationDigestTests
{
    [Fact]
    public void 依種類標題與主體累計次數耗時失敗與降級()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        Run(center, ref now, 100);
        Run(center, ref now, 300);
        Run(center, ref now, 200, scope => scope.Degrade());
        Run(center, ref now, 400, scope => scope.Fail());
        Run(center, ref now, 50, subject: "dbo.Copy");

        var entries = center.Digest.Snapshot();
        Assert.Equal(2, entries.Count);
        var loan = entries[0];
        Assert.Equal(NotificationKind.Metadata, loan.Kind);
        Assert.Equal(NotificationCatalog.LoadingColumns, loan.Title);
        Assert.Equal("dbo.Loan", loan.Subject);
        Assert.Equal(4, loan.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), loan.Total);
        Assert.Equal(TimeSpan.FromMilliseconds(250), loan.Average);
        Assert.Equal(TimeSpan.FromMilliseconds(400), loan.Max);
        Assert.Equal(1, loan.Failed);
        Assert.Equal(1, loan.Degraded);
        Assert.Equal("dbo.Copy", entries[1].Subject);
        Assert.False(loan.IsOther);
    }

    [Fact]
    public void 可依次數或總耗時排序()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        Run(center, ref now, 20, subject: "dbo.Loan");
        Run(center, ref now, 20, subject: "dbo.Loan");
        Run(center, ref now, 20, subject: "dbo.Loan");
        Run(center, ref now, 900, subject: "dbo.Copy");

        Assert.Equal("dbo.Loan", center.Digest.Snapshot(NotificationDigestOrder.Count)[0].Subject);
        Assert.Equal("dbo.Copy", center.Digest.Snapshot(NotificationDigestOrder.TotalElapsed)[0].Subject);
    }

    [Fact]
    public void 隱藏與只進統計的工作一樣計入()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        var settings = new SqlAssistSettings();
        // Completion 的種類開關預設關著，快取命中又是只進統計的 Trace。
        using (center.BeginDetached(NotificationCatalog.PreparingSuggestions, NotificationKind.Completion,
            NotificationOrigin.Typing, NotificationLevel.Debug)) { }
        using (center.BeginDetached(NotificationCatalog.CacheHit, NotificationKind.Metadata,
            NotificationOrigin.Typing, NotificationLevel.Trace, subject: "dbo.Loan")) { }

        Assert.DoesNotContain(center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue),
            item => NotificationVisibility.Includes(item, settings));
        Assert.Equal(new[] { NotificationCatalog.CacheHit, NotificationCatalog.PreparingSuggestions },
            center.Digest.Snapshot().Select(entry => entry.Title).OrderBy(title => title, StringComparer.Ordinal).ToArray());
        Assert.All(center.Digest.Snapshot(), entry => Assert.Equal(1, entry.Count));
    }

    [Fact]
    public void 畫面合併不改變統計次數()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        for (var index = 0; index < 5; index++) Run(center, ref now, 10);

        Assert.Single(NotificationMerge.Collapse(center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue)));
        Assert.Equal(5, Assert.Single(center.Digest.Snapshot()).Count);
    }

    [Fact]
    public void 超過上限併入其他並排在最後()
    {
        var digest = new NotificationDigest(capacity: 2);
        digest.Record(Item("dbo.Loan", 10));
        digest.Record(Item("dbo.Copy", 10));
        digest.Record(Item("dbo.Branch", 500));
        digest.Record(Item("dbo.Tag", 500, scope => scope.Fail()));
        digest.Record(Item("dbo.Loan", 10));

        var entries = digest.Snapshot(NotificationDigestOrder.TotalElapsed);
        Assert.Equal(3, entries.Count);
        // 併進「其他」的耗時最長，但那一列固定排在最後，不與可分辨的工作競爭順序。
        Assert.All(entries.Take(2), entry => Assert.False(entry.IsOther));
        var other = entries[2];
        Assert.True(other.IsOther);
        Assert.Equal(NotificationDigest.OtherTitle, other.Title);
        Assert.Empty(other.Subject);
        Assert.Equal(2, other.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), other.Total);
        Assert.Equal(1, other.Failed);
        Assert.Equal(2, entries[0].Count);
    }

    private static NotificationItem Item(string subject, int milliseconds,
        Action<NotificationScope>? result = null)
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        using (var scope = center.BeginDetached(NotificationCatalog.LoadingColumns, NotificationKind.Metadata,
            NotificationOrigin.Typing, NotificationLevel.Info, subject))
        {
            now += TimeSpan.FromMilliseconds(milliseconds);
            result?.Invoke(scope);
        }

        return Assert.Single(center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue));
    }

    private static void Run(NotificationCenter center, ref DateTimeOffset now, int milliseconds,
        Action<NotificationScope>? result = null, string subject = "dbo.Loan")
    {
        using var scope = center.BeginDetached(NotificationCatalog.LoadingColumns, NotificationKind.Metadata,
            NotificationOrigin.Typing, NotificationLevel.Info, subject);
        now += TimeSpan.FromMilliseconds(milliseconds);
        result?.Invoke(scope);
    }
}
