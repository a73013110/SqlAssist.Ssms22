using System;
using SqlAssist.Ssms22.Notifications;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Notifications;

public sealed class NotificationIslandStateTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 沒有內容是隱藏執行中是膠囊結束後是完成()
    {
        var state = new NotificationIslandState();
        Assert.False(state.Update(Input(), Start));
        Assert.Equal(NotificationIslandShape.Hidden, state.Shape);
        Assert.True(state.Update(Input(activities: 2, running: 1), Start));
        Assert.Equal(NotificationIslandShape.Compact, state.Shape);
        state.Update(Input(activities: 2), Start);
        Assert.Equal(NotificationIslandShape.Done, state.Shape);
        state.Update(Input(), Start);
        Assert.Equal(NotificationIslandShape.Hidden, state.Shape);
    }

    [Fact]
    public void 停駐三百毫秒展開移開六百毫秒收回()
    {
        var state = new NotificationIslandState();
        state.Update(Input(activities: 1, running: 1), Start);
        state.PointerEntered(Start);
        Assert.Equal(Start + NotificationIslandState.ExpandDelay, state.Deadline);
        Assert.False(state.Tick(Start + TimeSpan.FromMilliseconds(299)));
        Assert.Equal(NotificationIslandShape.Compact, state.Shape);
        Assert.True(state.Tick(Start + TimeSpan.FromMilliseconds(300)));
        Assert.Equal(NotificationIslandShape.Expanded, state.Shape);

        var left = Start + TimeSpan.FromSeconds(2);
        state.PointerExited(left);
        Assert.Equal(left + NotificationIslandState.CollapseDelay, state.Deadline);
        state.Tick(left + TimeSpan.FromMilliseconds(599));
        Assert.Equal(NotificationIslandShape.Expanded, state.Shape);
        // 在收回之前移回來就取消收回。
        state.PointerEntered(left + TimeSpan.FromMilliseconds(599));
        state.Tick(left + TimeSpan.FromSeconds(5));
        Assert.Equal(NotificationIslandShape.Expanded, state.Shape);
        state.PointerExited(left + TimeSpan.FromSeconds(5));
        state.Tick(left + TimeSpan.FromMilliseconds(5600));
        Assert.Equal(NotificationIslandShape.Compact, state.Shape);
        Assert.Null(state.Deadline);
    }

    [Fact]
    public void 路過不展開而鍵盤焦點立即展開()
    {
        var state = new NotificationIslandState();
        state.Update(Input(activities: 1), Start);
        state.PointerEntered(Start);
        state.PointerExited(Start + TimeSpan.FromMilliseconds(200));
        state.Tick(Start + TimeSpan.FromSeconds(1));
        Assert.Equal(NotificationIslandShape.Done, state.Shape);

        state.FocusChanged(true, Start + TimeSpan.FromSeconds(2));
        Assert.Equal(NotificationIslandShape.Expanded, state.Shape);
        state.Tick(Start + TimeSpan.FromSeconds(10));
        Assert.Equal(NotificationIslandShape.Expanded, state.Shape);
        state.FocusChanged(false, Start + TimeSpan.FromSeconds(10));
        state.Tick(Start + TimeSpan.FromMilliseconds(10600));
        Assert.Equal(NotificationIslandShape.Done, state.Shape);
    }

    [Fact]
    public void 失敗不展開只換警告並短震一次()
    {
        var state = new NotificationIslandState();
        state.Update(Input(activities: 2, running: 1), Start);
        Assert.False(state.Warning);
        Assert.True(state.Update(Input(activities: 2, running: 1, failed: 1), Start));
        Assert.Equal(NotificationIslandShape.Compact, state.Shape);
        Assert.True(state.Warning);
        Assert.Equal(1, state.ShakeCount);
        // 同一個失敗重畫不再震。
        Assert.False(state.Update(Input(activities: 2, running: 1, failed: 1), Start));
        Assert.Equal(1, state.ShakeCount);
        state.Update(Input(activities: 3, failed: 2), Start);
        Assert.Equal(2, state.ShakeCount);
        Assert.Equal(NotificationIslandShape.Done, state.Shape);
    }

    [Fact]
    public void 有提醒時活動縮成附條且停駐不展開活動()
    {
        var state = new NotificationIslandState();
        state.Update(Input(prompts: 1), Start);
        Assert.Equal(NotificationIslandShape.Prompt, state.Shape);
        Assert.False(state.ActivityStrip);

        state.Update(Input(activities: 1, running: 1, prompts: 1), Start);
        Assert.Equal(NotificationIslandShape.PromptWithActivity, state.Shape);
        Assert.True(state.ActivityStrip);
        state.PointerEntered(Start);
        state.Tick(Start + TimeSpan.FromSeconds(1));
        Assert.Equal(NotificationIslandShape.PromptWithActivity, state.Shape);

        state.Update(Input(activities: 1, running: 1, prompts: 3), Start);
        Assert.Equal(NotificationIslandShape.PromptStack, state.Shape);
        Assert.True(state.ActivityStrip);
        state.Update(Input(prompts: 2), Start);
        Assert.Equal(NotificationIslandShape.PromptStack, state.Shape);
        Assert.False(state.ActivityStrip);
    }

    [Fact]
    public void 按附條暫時看活動移開後回到提醒()
    {
        var state = new NotificationIslandState();
        state.Update(Input(activities: 2, running: 1, prompts: 1), Start);
        state.PointerEntered(Start);
        Assert.True(state.TogglePeek(Start));
        Assert.True(state.Peeking);
        Assert.False(state.ActivityStrip);
        Assert.Equal(NotificationIslandShape.Expanded, state.Shape);

        state.PointerExited(Start + TimeSpan.FromSeconds(1));
        state.Tick(Start + TimeSpan.FromMilliseconds(1599));
        Assert.Equal(NotificationIslandShape.Expanded, state.Shape);
        state.Tick(Start + TimeSpan.FromMilliseconds(1600));
        Assert.Equal(NotificationIslandShape.PromptWithActivity, state.Shape);

        // 再按一次直接切回；活動結束也收掉暫看。
        state.TogglePeek(Start + TimeSpan.FromSeconds(3));
        state.TogglePeek(Start + TimeSpan.FromSeconds(3));
        Assert.Equal(NotificationIslandShape.PromptWithActivity, state.Shape);
        state.TogglePeek(Start + TimeSpan.FromSeconds(4));
        state.Update(Input(prompts: 1), Start + TimeSpan.FromSeconds(4));
        Assert.False(state.Peeking);
        Assert.Equal(NotificationIslandShape.Prompt, state.Shape);
    }

    [Fact]
    public void 沒有活動時按附條不做事()
    {
        var state = new NotificationIslandState();
        state.Update(Input(prompts: 1), Start);
        Assert.False(state.TogglePeek(Start));
        Assert.Equal(NotificationIslandShape.Prompt, state.Shape);
    }

    private static NotificationIslandInput Input(int activities = 0, int running = 0, int failed = 0, int prompts = 0) =>
        new(activities, running, failed, prompts);
}
