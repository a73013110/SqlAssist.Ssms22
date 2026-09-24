using System;
using System.Linq;
using System.Threading.Tasks;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Notifications;

public sealed class NotificationRehearsalTests
{
    private static readonly Func<TimeSpan, Task> Instant = _ => Task.CompletedTask;

    [Fact]
    public void 每一種情境都在按鈕表上且有標籤()
    {
        Assert.Equal(Enum.GetValues(typeof(NotificationRehearsalScenario)).Cast<NotificationRehearsalScenario>(),
            NotificationRehearsal.All.OrderBy(x => x));
        Assert.All(NotificationRehearsal.All, x => Assert.NotEmpty(NotificationRehearsal.Label(x)));
    }

    [Fact]
    public async Task 失敗情境走正式的完成路徑並列進最近失敗()
    {
        var center = new NotificationCenter();
        await NotificationRehearsal.RunAsync(NotificationRehearsalScenario.Success, center, Instant);
        await NotificationRehearsal.RunAsync(NotificationRehearsalScenario.Failure, center, Instant);

        var items = Activities(center);
        Assert.Equal(new[] { NotificationStatus.Succeeded, NotificationStatus.Failed }, items.Select(x => x.Status));
        Assert.All(items, x => Assert.Equal(NotificationKind.Diagnostics, x.Kind));
        Assert.Equal(NotificationKind.Diagnostics, Assert.Single(center.RecentFailures).Kind);
        Assert.Equal("通知測試", NotificationKindToggle.Label(NotificationKind.Diagnostics));
    }

    [Fact]
    public async Task 連續成功併成一列()
    {
        var center = new NotificationCenter();
        await NotificationRehearsal.RunAsync(NotificationRehearsalScenario.Repeats, center, Instant);
        // 單獨的成功情境是另一個標題，不會把 ×N 灌大。
        await NotificationRehearsal.RunAsync(NotificationRehearsalScenario.Success, center, Instant);

        var merged = NotificationMerge.Collapse(Activities(center));
        Assert.Equal(2, merged.Count);
        Assert.Equal(NotificationRehearsal.RepeatCount, merged.Single(x => x.Title == NotificationCatalog.SimulatingRepeatedWork).Repeat);
    }

    [Fact]
    public async Task 三則提醒各自一個鍵且按知道了就收掉()
    {
        var center = new NotificationCenter();
        await NotificationRehearsal.RunAsync(NotificationRehearsalScenario.PromptStack, center, Instant);
        var prompts = Prompts(center);
        Assert.Equal(3, prompts.Length);
        Assert.Equal(3, prompts.Select(x => x.Key).Distinct().Count());
        Assert.Equal(3, prompts.Select(x => x.Severity).Distinct().Count());

        Assert.True(center.Resolve(prompts[0].Id, NotificationActionIds.RehearsalAcknowledge));
        // 一則提醒的情境取代同鍵那一則，不會變成四則。
        await NotificationRehearsal.RunAsync(NotificationRehearsalScenario.Prompt, center, Instant);
        Assert.Equal(3, Prompts(center).Length);
    }

    [Fact]
    public async Task 叉號收起之後再按一次仍然出現()
    {
        var center = new NotificationCenter();
        await NotificationRehearsal.RunAsync(NotificationRehearsalScenario.Prompt, center, Instant);
        Assert.True(center.Resolve(Assert.Single(Prompts(center)).Id, null));
        await NotificationRehearsal.RunAsync(NotificationRehearsalScenario.Prompt, center, Instant);
        Assert.Single(Prompts(center));
    }

    [Fact]
    public async Task 提醒加活動在工作期間兩者並存()
    {
        var center = new NotificationCenter();
        var gate = new TaskCompletionSource<bool>();
        var running = NotificationRehearsal.RunAsync(NotificationRehearsalScenario.PromptWithActivity, center, _ => gate.Task);

        Assert.Single(Prompts(center));
        Assert.Equal(NotificationStatus.Running, Assert.Single(Activities(center)).Status);
        gate.SetResult(true);
        await running;
        Assert.Equal(NotificationStatus.Succeeded, Assert.Single(Activities(center)).Status);
    }

    [Fact]
    public void 種類開關與詳細度擋不住測試而總開關與失敗通道說得出原因()
    {
        var center = new NotificationCenter();
        using (center.BeginDetached(NotificationCatalog.SimulatingWork, NotificationKind.Diagnostics,
                   NotificationOrigin.User, NotificationLevel.Info)) { }
        var quietAndOff = new SqlAssistSettings
        {
            NotificationKinds = NotificationKindToggle.All.Aggregate(NotificationKindSwitches.Defaults,
                (switches, toggle) => switches.With(toggle.Kind, false)),
            NotificationVerbosity = NotificationVerbosity.Quiet,
        };
        Assert.True(NotificationVisibility.Includes(Assert.Single(Activities(center)), quietAndOff));
        Assert.All(NotificationRehearsal.All, x => Assert.Null(NotificationRehearsal.HiddenReason(x, quietAndOff)));

        var off = new SqlAssistSettings { NotificationEnabled = false };
        Assert.All(NotificationRehearsal.All, x => Assert.NotNull(NotificationRehearsal.HiddenReason(x, off)));

        var noFailures = new SqlAssistSettings { NotificationFailures = false };
        Assert.Equal(new[] { NotificationRehearsalScenario.Failure },
            NotificationRehearsal.All.Where(x => NotificationRehearsal.HiddenReason(x, noFailures) is not null));
    }

    private static NotificationItem[] Activities(NotificationCenter center) =>
        center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue).Where(x => !x.IsPrompt).ToArray();

    private static NotificationItem[] Prompts(NotificationCenter center) =>
        center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue).Where(x => x.IsPrompt).ToArray();
}
