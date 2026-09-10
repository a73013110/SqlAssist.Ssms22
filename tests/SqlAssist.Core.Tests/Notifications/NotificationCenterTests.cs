using System;
using System.Globalization;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SqlAssist.Core.Notifications;
using Xunit;

namespace SqlAssist.Core.Tests.Notifications;

public sealed class NotificationCenterTests
{
    [Fact]
    public void 進度訊息快照不可變且完成後保留()
    {
        var center = new NotificationCenter();
        var scope = center.Begin("載入欄位與定義", NotificationKind.Metadata, NotificationOrigin.Typing,
            NotificationLevel.Info);
        var before = Assert.Single(Read(center));
        scope.Report("正在整理欄位資訊");
        Assert.Empty(before.Message);
        Assert.Equal("正在整理欄位資訊", Assert.Single(Read(center)).Message);
        scope.Report(new string('長', 600));
        Assert.Equal(512, Assert.Single(Read(center)).Message.Length);
        scope.Dispose();
        scope.Report("不應覆寫完成內容");
        Assert.Equal(512, Assert.Single(Read(center)).Message.Length);
    }

    [Fact]
    public void 標題主體與來源分開保存()
    {
        var center = new NotificationCenter();
        using (center.Begin("載入欄位與定義", NotificationKind.Metadata, NotificationOrigin.User,
            NotificationLevel.Info, subject: "dbo.Loan", context: "LibArchive")) { }
        var item = Assert.Single(Read(center));
        Assert.Equal("載入欄位與定義", item.Title);
        Assert.Equal("dbo.Loan", item.Subject);
        Assert.Equal("LibArchive", item.Context);
        Assert.DoesNotContain("dbo.Loan", item.Title);
    }

    [Fact]
    public void 懸停後續跑剩餘時間並保留真實完成時間()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        using (Begin(center, "載入物件清單")) { }
        var finished = now;
        now += TimeSpan.FromSeconds(1);
        center.Snapshot(TimeSpan.FromMilliseconds(2500), TimeSpan.FromSeconds(6), retain: true);
        now += TimeSpan.FromSeconds(10);
        Assert.Equal(finished, Assert.Single(Read(center)).Finished);
        now += TimeSpan.FromMilliseconds(1499);
        Assert.Single(Read(center));
        now += TimeSpan.FromMilliseconds(1);
        Assert.Empty(Read(center));
    }

    [Fact]
    public void 懸停期間才完成的工作只暫停完成後時間()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        var scope = Begin(center, "載入欄位與定義");
        center.Snapshot(TimeSpan.FromMilliseconds(2500), TimeSpan.FromSeconds(6), retain: true);
        now += TimeSpan.FromSeconds(10);
        scope.Dispose();
        now += TimeSpan.FromSeconds(10);
        Assert.Single(Read(center));
        now += TimeSpan.FromMilliseconds(2500);
        Assert.Empty(Read(center));
    }

    [Fact]
    public void 完成與總數一起到期且失敗另行保留()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        using (Begin(center, "載入物件清單")) { }
        using (var failed = Begin(center, "載入欄位與定義")) failed.Fail();
        var running = Begin(center, "載入索引與條件約束");
        Assert.Equal(3, Read(center).Count);
        Assert.Single(Read(center), x => x.Status == NotificationStatus.Succeeded);
        now += TimeSpan.FromSeconds(3);
        Assert.Equal(2, Read(center).Count);
        now += TimeSpan.FromSeconds(4);
        Assert.Single(Read(center));
        Assert.Single(center.RecentFailures);
        running.Cancel(); running.Dispose();
        Assert.Equal(NotificationStatus.Canceled, Assert.Single(Read(center)).Status);
    }

    [Fact]
    public void 降級沿用較長的期限且不進失敗列表()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        using (var degraded = Begin(center, "載入欄位與定義")) degraded.Degrade();
        now += TimeSpan.FromSeconds(3);
        Assert.Equal(NotificationStatus.Degraded, Assert.Single(Read(center)).Status);
        now += TimeSpan.FromSeconds(4);
        Assert.Empty(Read(center));
        Assert.Empty(center.RecentFailures);
    }

    [Fact]
    public void 合併子工作失敗只讓父工作降級()
    {
        var center = new NotificationCenter();
        using (Begin(center, "展開萬用字元", NotificationKind.Editing, NotificationOrigin.User))
        {
            using (var nested = Begin(center, "載入欄位與定義", NotificationKind.Metadata, joinParent: true)) nested.Fail();
            Assert.Equal(NotificationStatus.Running, Assert.Single(Read(center)).Status);
        }
        var item = Assert.Single(Read(center));
        Assert.Equal(NotificationStatus.Degraded, item.Status);
        Assert.Equal(NotificationSeverity.Warning, item.Severity);
        // 降級不是失敗，不進最近失敗列表，種類也還是父工作的。
        Assert.Empty(center.RecentFailures);
        Assert.Equal(NotificationKind.Editing, item.Kind);
    }

    [Fact]
    public void 父工作自己的例外仍是失敗且蓋過降級()
    {
        var center = new NotificationCenter();
        using (var parent = Begin(center, "展開語句樣板", NotificationKind.Editing, NotificationOrigin.User))
        {
            using (var nested = Begin(center, "載入欄位與定義", NotificationKind.Metadata, joinParent: true)) nested.Fail();
            parent.Fail();
        }
        var item = Assert.Single(Read(center));
        Assert.Equal(NotificationStatus.Failed, item.Status);
        Assert.Equal(NotificationSeverity.Error, item.Severity);
        Assert.Single(center.RecentFailures);
    }

    [Fact]
    public void 獨立背景工作不因父工作完成而消失且來源不跟著焦點改變()
    {
        var center = new NotificationCenter();
        var parent = Begin(center, "初始化 SqlAssist", context: "SQL 編輯區 1");
        var child = Begin(center, "建立中繼資料連線", context: "LibArchive");
        parent.Dispose();
        Assert.Equal(2, Read(center).Count);
        Assert.Equal("LibArchive", Assert.Single(Read(center), x => x.Status == NotificationStatus.Running).Context);
        child.Fail(); child.Dispose();
        Assert.Single(Read(center), x => x.Status == NotificationStatus.Succeeded);
        Assert.Single(Read(center), x => x.Status == NotificationStatus.Failed);
    }

    [Fact]
    public void 巢狀取消不會誤報成功也不算降級()
    {
        var center = new NotificationCenter();
        using (Begin(center, "載入物件清單"))
            using (var nested = Begin(center, "載入資料庫清單", joinParent: true)) nested.Cancel();
        var item = Assert.Single(Read(center));
        Assert.Equal(NotificationStatus.Canceled, item.Status);
        Assert.Equal(NotificationSeverity.Info, item.Severity);
        Assert.Empty(center.RecentFailures);
    }

    [Fact]
    public void 並行子工作的降級不會被取消蓋掉()
    {
        var center = new NotificationCenter();
        using (Begin(center, "展開萬用字元", NotificationKind.Editing, NotificationOrigin.User))
        {
            using (var failed = Begin(center, "載入欄位與定義", joinParent: true)) failed.Fail();
            using (var canceled = Begin(center, "載入索引與條件約束", joinParent: true)) canceled.Cancel();
        }
        Assert.Equal(NotificationStatus.Degraded, Assert.Single(Read(center)).Status);
    }

    [Fact]
    public async Task 並行工作各自完成且重複釋放無害()
    {
        var center = new NotificationCenter();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            var scope = Begin(center, "載入欄位與定義", subject: "dbo.Loan" + i);
            await Task.Yield();
            scope.Dispose(); scope.Dispose();
        })));
        Assert.Equal(40, Read(center).Count);
        Assert.All(Read(center), item => Assert.Equal(NotificationStatus.Succeeded, item.Status));
    }

    [Fact]
    public void 閱讀期間保留結果且無編輯器時紀錄仍有上限()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        using (Begin(center, "載入物件清單")) { }
        now += TimeSpan.FromMinutes(1);
        Assert.Single(center.Snapshot(TimeSpan.Zero, TimeSpan.Zero, retain: true));
        Assert.Empty(Read(center));
        for (var i = 0; i < 200; i++)
            using (var scope = Begin(center, "載入物件清單")) scope.Fail();
        Assert.Equal(100, Read(center).Count);
        Assert.Equal(30, center.RecentFailures.Count);
    }

    [Fact]
    public void 並行工作不接手環境父工作()
    {
        var center = new NotificationCenter();
        using (Begin(center, NotificationCatalog.LoadingObjects))
        {
            using (center.BeginDetached(NotificationCatalog.QueryingLinkedServer, NotificationKind.Metadata,
                       NotificationOrigin.Typing, NotificationLevel.Notice, "LibMirror"))
            {
                // 並行工作沒有接手，巢狀查詢仍然併進真正的父工作而不是它。
                using (var nested = Begin(center, NotificationCatalog.LoadingDatabases, joinParent: true)) nested.Fail();
                Assert.Equal(2, Read(center).Count(x => x.Status == NotificationStatus.Running));
            }
        }

        var hop = Assert.Single(Read(center), x => x.Title == NotificationCatalog.QueryingLinkedServer);
        Assert.Equal(NotificationLevel.Notice, hop.Level);
        Assert.Equal("LibMirror", hop.Subject);
        Assert.Equal(NotificationStatus.Succeeded, hop.Status);
        Assert.Equal(NotificationStatus.Degraded,
            Assert.Single(Read(center), x => x.Title == NotificationCatalog.LoadingObjects).Status);
    }

    // 敘述只回答措辭：物件名稱是主體、重複次數是徽章，怎麼擺由通知卡片決定。
    [Theory]
    [InlineData(NotificationStatus.Running, "載入欄位與定義")]
    [InlineData(NotificationStatus.Succeeded, "已載入欄位與定義")]
    [InlineData(NotificationStatus.Failed, "載入欄位與定義")]
    public void 目錄依結果產生敘述(NotificationStatus status, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        using (var scope = center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata,
                   NotificationOrigin.Typing, NotificationLevel.Info, "dbo.Loan"))
        {
            if (status == NotificationStatus.Failed) scope.Fail();
            if (status == NotificationStatus.Running)
            {
                var running = Assert.Single(Read(center));
                Assert.Equal(expected, NotificationCatalog.Headline(running));
                Assert.Equal("dbo.Loan", running.Subject);
                return;
            }
        }

        var item = Assert.Single(Read(center));
        Assert.Equal(expected, NotificationCatalog.Headline(item));
        Assert.Equal("dbo.Loan", item.Subject);
        Assert.Equal(NotificationCatalog.StatusText(status), NotificationCatalog.StatusText(item.Status));
    }

    [Fact]
    public void 耗時超過一秒才寫進敘述()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        using (center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata,
                   NotificationOrigin.Typing, NotificationLevel.Info)) now += TimeSpan.FromMilliseconds(900);
        Assert.Equal("已載入物件清單", NotificationCatalog.Headline(Assert.Single(Read(center))));

        var slow = new NotificationCenter(() => now);
        using (slow.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata,
                   NotificationOrigin.Typing, NotificationLevel.Info)) now += TimeSpan.FromMilliseconds(1400);
        Assert.Contains("1.4 秒", NotificationCatalog.Headline(Assert.Single(
            slow.Snapshot(TimeSpan.FromMilliseconds(2500), TimeSpan.FromSeconds(6)))));
    }

    [Fact]
    public void 標題一律是常數短語且不含物件名稱與標點()
    {
        foreach (var title in typeof(NotificationCatalog)
                     .GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                     .Select(field => (string)field.GetRawConstantValue()!))
        {
            Assert.NotEmpty(title);
            Assert.DoesNotContain('{', title);
            Assert.DoesNotContain('。', title);
            Assert.DoesNotContain('，', title);
            Assert.DoesNotContain('·', title);
            Assert.DoesNotContain('（', title);
            // 十四個中文字的上限只算中日韓區段，「SELECT ＊」這類半形夾雜不受影響。
            Assert.True(title.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF) <= 14, title);
        }
    }

    [Fact]
    public void 內容沒變回傳同一份快照到期才換新()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        using (Begin(center, NotificationCatalog.LoadingObjects)) { }
        var first = Read(center);
        // 提示每 100 ms 問一次；沒有新工作也沒有到期時不該每次重新配置。
        now += TimeSpan.FromMilliseconds(100);
        Assert.Same(first, Read(center));

        using (Begin(center, NotificationCatalog.LoadingColumns)) { }
        var second = Read(center);
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);

        // 保留期限到了就算沒有任何新事件也要換一份，否則過期的結果會留在畫面上。
        now += TimeSpan.FromMilliseconds(2500);
        var expired = Read(center);
        Assert.NotSame(second, expired);
        Assert.Empty(expired);
    }

    [Fact]
    public void 停留在提示上時期限暫停且不吃快取()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        using (Begin(center, NotificationCatalog.LoadingObjects)) { }
        Assert.Single(center.Snapshot(TimeSpan.FromMilliseconds(2500), TimeSpan.FromSeconds(6), retain: true));
        now += TimeSpan.FromMilliseconds(2400);
        Assert.Single(center.Snapshot(TimeSpan.FromMilliseconds(2500), TimeSpan.FromSeconds(6), retain: true));
        now += TimeSpan.FromMilliseconds(2400);
        // 閱讀期間不計入期限：移出之後才開始跑剩下的時間。
        Assert.Single(Read(center));
        now += TimeSpan.FromMilliseconds(2500);
        Assert.Empty(Read(center));
    }

    [Fact]
    public void 已完成的項數超過上限就從最舊的截掉()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        using var running = Begin(center, NotificationCatalog.LoadingObjects);
        for (var index = 0; index < 140; index++)
            using (Begin(center, NotificationCatalog.LoadingColumns, subject: index.ToString(CultureInfo.InvariantCulture))) { }
        var items = center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue);
        // 執行中的不能被截掉；已完成的維持上限。
        Assert.Equal(101, items.Count);
        Assert.Single(items, x => x.Status == NotificationStatus.Running);
        Assert.Equal("40", items[1].Subject);
    }

    private static NotificationScope Begin(NotificationCenter center, string title,
        NotificationKind kind = NotificationKind.Metadata, NotificationOrigin origin = NotificationOrigin.Ambient,
        string subject = "", string context = "", bool joinParent = false) =>
        center.Begin(title, kind, origin, NotificationLevel.Info, subject, context, joinParent);

    private static IReadOnlyList<NotificationItem> Read(NotificationCenter center) =>
        center.Snapshot(TimeSpan.FromMilliseconds(2500), TimeSpan.FromSeconds(6));
}
