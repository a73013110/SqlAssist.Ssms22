using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Notifications;

public sealed class NotificationPromptTests
{
    [Fact]
    public void 同種類同鍵取代舊的那一則且換新身分()
    {
        var center = new NotificationCenter();
        var first = Update(center, "1.4.0")!;
        var second = Update(center, "1.5.0")!;
        var prompt = Assert.Single(Prompts(center));
        Assert.Equal(second.Id, prompt.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Contains("1.5.0", prompt.Message);
        Assert.False(center.Resolve(first.Id, null));

        // 鍵只在同一個種類內比較：另一個種類用同一個字串是另一件事。
        center.Prompt(NotificationCatalog.UpdateAvailablePrompt("1.5.0", Url), NotificationKind.Unclassified,
            NotificationOrigin.Ambient, NotificationLevel.Notice);
        Assert.Equal(2, Prompts(center).Count);
    }

    [Fact]
    public void 按下按鈕收掉這一則並回報識別字()
    {
        var center = new NotificationCenter();
        var resolved = new List<(long, string?)>();
        center.Resolved += (item, action) => resolved.Add((item.Id, action));
        var prompt = Update(center, "1.4.0")!;
        Assert.Throws<ArgumentException>(() => center.Resolve(prompt.Id, "update.unknown"));
        Assert.Single(Prompts(center));

        Assert.True(center.Resolve(prompt.Id, NotificationActionIds.UpdateSkip));
        Assert.Empty(Prompts(center));
        Assert.Equal((prompt.Id, NotificationActionIds.UpdateSkip), Assert.Single(resolved));
        Assert.Equal("1.4.0", prompt.Actions.Single(x => x.Id == NotificationActionIds.UpdateSkip).Argument);
        Assert.False(center.Resolve(prompt.Id, NotificationActionIds.UpdateSkip));

        // 按鈕的決定由呼叫端保存；同鍵的下一則照樣送得出來。
        Assert.NotNull(Update(center, "1.5.0"));
    }

    [Fact]
    public void 叉號是稍後只在這次工作階段收起同鍵提醒()
    {
        var center = new NotificationCenter();
        string? action = "未觸發";
        center.Resolved += (_, id) => action = id;
        var prompt = Update(center, "1.4.0")!;
        Assert.True(center.Resolve(prompt.Id, null));
        Assert.Null(action);
        Assert.Null(Update(center, "1.5.0"));
        Assert.Empty(Prompts(center));

        // 其他鍵不受影響；下次啟動就是一個新的 NotificationCenter。
        Assert.NotNull(center.Prompt(NotificationCatalog.SqlMemoryCapacityPrompt(""), NotificationKind.SqlMemory,
            NotificationOrigin.Ambient, NotificationLevel.Notice));
        Assert.NotNull(Update(new NotificationCenter(), "1.5.0"));
    }

    /// <summary>使用者自己按「檢查更新」得到的答案，不能因為稍早按過叉號就安靜地消失。</summary>
    [Fact]
    public void 使用者觸發的提醒不理會稍後並解除它()
    {
        var center = new NotificationCenter();
        Assert.True(center.Resolve(Update(center, "1.4.0")!.Id, null));
        Assert.Null(Update(center, "1.4.0"));
        Assert.NotNull(center.Prompt(NotificationCatalog.UpdateAvailablePrompt("1.4.0", Url), NotificationKind.Update,
            NotificationOrigin.User, NotificationLevel.Info));
        // 解除之後，同鍵的自動提醒也照常出現。
        Assert.NotNull(Update(center, "1.5.0"));
    }

    [Fact]
    public void 提醒不套用保留期限且活動照常到期()
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenter(() => now);
        using (center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing,
            NotificationLevel.Info)) { }
        Update(center, "1.4.0");
        now += TimeSpan.FromDays(3);
        var items = center.Snapshot(TimeSpan.FromMilliseconds(2500), TimeSpan.FromSeconds(6));
        Assert.True(Assert.Single(items).IsPrompt);
    }

    [Fact]
    public void 提醒另有上限且不跟最近一百項搶位置()
    {
        var center = new NotificationCenter();
        var prompts = Enumerable.Range(0, NotificationCenter.PromptLimit + 5)
            .Select(i => center.Prompt(Capacity(i), NotificationKind.SqlMemory, NotificationOrigin.Ambient,
                NotificationLevel.Notice)!)
            .ToArray();
        for (var i = 0; i < 150; i++)
            using (center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata, NotificationOrigin.Typing,
                NotificationLevel.Info)) { }

        var kept = Prompts(center);
        Assert.Equal(NotificationCenter.PromptLimit, kept.Count);
        Assert.Equal(prompts.Skip(5).Select(x => x.Id), kept.Select(x => x.Id));
        Assert.Equal(100, center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue).Count(x => !x.IsPrompt));
    }

    [Fact]
    public void 提醒只看總開關與種類開關()
    {
        var prompt = new NotificationCenter().Prompt(NotificationCatalog.SqlMemoryCapacityPrompt(""),
            NotificationKind.SqlMemory, NotificationOrigin.Ambient, NotificationLevel.Debug)!;
        Assert.Equal(NotificationSeverity.Warning, prompt.Severity);
        foreach (var settings in new[]
        {
            new SqlAssistSettings(),
            new SqlAssistSettings { NotificationVerbosity = NotificationVerbosity.Quiet },
            new SqlAssistSettings { NotificationDegraded = false, NotificationFailures = false },
        })
            Assert.True(NotificationVisibility.Includes(prompt, settings));

        foreach (var settings in new[]
        {
            new SqlAssistSettings { NotificationEnabled = false },
            new SqlAssistSettings { Enabled = false },
            new SqlAssistSettings { NotificationKinds = NotificationKindSwitches.Defaults.With(NotificationKind.SqlMemory, false) },
        })
            Assert.False(NotificationVisibility.Includes(prompt, settings));
    }

    [Fact]
    public void 合併時提醒不併進同名活動()
    {
        var center = new NotificationCenter();
        var prompt = center.Prompt(NotificationCatalog.SqlMemoryFirstCapturePrompt(), NotificationKind.SqlMemory,
            NotificationOrigin.Ambient, NotificationLevel.Notice)!;
        for (var i = 0; i < 2; i++)
            center.Post(prompt.Title, NotificationKind.SqlMemory, NotificationOrigin.Ambient, NotificationLevel.Notice,
                NotificationStatus.Succeeded);

        var merged = NotificationMerge.Collapse(center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue));
        Assert.Equal(2, merged.Count);
        Assert.Equal(2, merged.Single(x => !x.IsPrompt).Repeat);
        var kept = merged.Single(x => x.IsPrompt);
        Assert.Equal(1, kept.Repeat);
        Assert.Same(prompt.Actions, kept.Actions);
    }

    [Fact]
    public void 提醒不進統計最近失敗與逐次完成紀錄()
    {
        var center = new NotificationCenter();
        var completed = 0;
        center.Completed += _ => completed++;
        center.Prompt(NotificationCatalog.SqlMemoryCapacityPrompt(""), NotificationKind.SqlMemory,
            NotificationOrigin.Ambient, NotificationLevel.Notice);
        Assert.Equal(0, completed);
        Assert.Empty(center.Digest.Snapshot());
        Assert.Empty(center.RecentFailures);
    }

    [Fact]
    public void 提醒內容的契約()
    {
        Assert.Throws<ArgumentException>(() => new NotificationPrompt("k", "標題", "", NotificationSeverity.Info));
        Assert.Throws<ArgumentException>(() => new NotificationPrompt("k", "標題", "", NotificationSeverity.Info,
            new NotificationAction("a", "甲", NotificationActionRole.Primary),
            new NotificationAction("b", "乙", NotificationActionRole.Primary)));
        Assert.Throws<ArgumentException>(() => new NotificationPrompt("k", "標題", "", NotificationSeverity.Info,
            new NotificationAction("a", "甲", NotificationActionRole.Secondary),
            new NotificationAction("a", "乙", NotificationActionRole.Primary)));
        Assert.Throws<ArgumentException>(() => new NotificationAction("", "甲", NotificationActionRole.Primary));

        var update = NotificationCatalog.UpdateAvailablePrompt("1.4.0", Url);
        Assert.Equal("update", update.Key);
        Assert.Equal(new[] { "略過此版本", "前往下載" }, update.Actions.Select(x => x.Label));
        Assert.Equal("1.4.0", update.Actions.Single(x => x.Id == NotificationActionIds.UpdateSkip).Argument);
        Assert.Equal(1, update.Actions.Count(x => x.Role == NotificationActionRole.Primary));
        Assert.Equal(Url, update.Actions.Single(x => x.Id == NotificationActionIds.UpdateDownload).Argument);
        Assert.False(new NotificationCenter().Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue).Any());
    }

    [Fact]
    public void 膠囊摘要一項說是哪件事多項說規模與進度()
    {
        var center = new NotificationCenter();
        Assert.Equal("", NotificationCatalog.CapsuleSummary(Array.Empty<NotificationItem>()));
        using (center.Begin(NotificationCatalog.LoadingColumns, NotificationKind.Metadata, NotificationOrigin.User,
            NotificationLevel.Info, subject: "dbo.Loan")) { }
        var one = center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue);
        Assert.Equal("已載入欄位與定義 · dbo.Loan", NotificationCatalog.CapsuleSummary(one));

        using var running = center.Begin(NotificationCatalog.LoadingIndexes, NotificationKind.Metadata,
            NotificationOrigin.User, NotificationLevel.Info);
        using (var failed = center.Begin(NotificationCatalog.LoadingObjects, NotificationKind.Metadata,
            NotificationOrigin.User, NotificationLevel.Info)) failed.Fail();
        Assert.Equal("3 項工作 · 1/3", NotificationCatalog.CapsuleSummary(center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue)));
        Assert.Equal("2/5", NotificationCatalog.PromptPosition(2, 5));
    }

    /// <summary>膠囊變形成清單時抬頭說的是同一句話，只有數字會變。</summary>
    [Fact]
    public void 清單抬頭與多項膠囊同一句話()
    {
        Assert.Equal("3 項工作 · 1/3", NotificationCatalog.ProgressSummary(1, 3));
        Assert.Equal("2 項失敗", NotificationCatalog.FailureSummary(2));
        Assert.Equal("1 則提醒待處理", NotificationCatalog.PendingPrompts(1));
    }

    private const string Url = "https://github.com/example/releases/latest";

    private static NotificationItem? Update(NotificationCenter center, string version) =>
        center.Prompt(NotificationCatalog.UpdateAvailablePrompt(version, Url), NotificationKind.Update,
            NotificationOrigin.Ambient, NotificationLevel.Notice);

    private static NotificationPrompt Capacity(int index) =>
        new("capacity." + index, "SQL Memory 超過容量警戒", "", NotificationSeverity.Warning,
            new NotificationAction(NotificationActionIds.SqlMemoryOpenMaintenance, "開啟維護", NotificationActionRole.Primary));

    private static IReadOnlyList<NotificationItem> Prompts(NotificationCenter center) =>
        center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue).Where(x => x.IsPrompt).ToArray();
}
