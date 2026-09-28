using System;
using SqlAssist.Core.Notifications;
using Xunit;

namespace SqlAssist.Core.Tests.Notifications;

/// <summary>
/// 平台邊界攔下的例外，使用者看得到一則失敗，但不會被洗版。
/// </summary>
/// <remarks>
/// 以前只寫紀錄檔：預覽建不起來時使用者只看到按向右鍵沒反應，要自己去翻紀錄檔。
/// </remarks>
public sealed class UnexpectedFailureNoticeTests
{
    private DateTimeOffset _now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>送出的是一則已失敗的事件，說的是「略過、看紀錄檔」，不帶操作名稱。</summary>
    [Fact]
    public void 送出一則失敗()
    {
        var center = new NotificationCenter(() => _now);

        Assert.True(new UnexpectedFailureNotice(center, () => _now).Report("建立結構預覽"));

        var item = Assert.Single(center.RecentFailures);
        Assert.Equal(NotificationStatus.Failed, item.Status);
        Assert.Equal(NotificationCatalog.ResultUnexpected, item.Message);
        Assert.Equal("", item.Subject);
    }

    /// <summary>
    /// 同一個操作隔滿間隔才再送；不同操作各算各的。
    /// </summary>
    /// <remarks>
    /// 按鍵處理常式壞掉時每按一次鍵失敗一次，不節流就是每個字一列失敗。
    /// </remarks>
    [Fact]
    public void 同一個操作在間隔內只送一次()
    {
        var center = new NotificationCenter(() => _now);
        var notice = new UnexpectedFailureNotice(center, () => _now);

        Assert.True(notice.Report("處理按鍵"));
        Assert.False(notice.Report("處理按鍵"));
        Assert.True(notice.Report("建立結構預覽"));

        _now += UnexpectedFailureNotice.Interval;
        Assert.True(notice.Report("處理按鍵"));
        Assert.Equal(3, center.RecentFailures.Count);
    }

    /// <summary>
    /// 送出的當下訂閱者又失敗（通知島重畫壞掉）不再送，否則遞迴到堆疊溢位。
    /// </summary>
    [Fact]
    public void 送出途中再失敗不遞迴()
    {
        var center = new NotificationCenter(() => _now);
        var notice = new UnexpectedFailureNotice(center, () => _now);
        bool? nested = null;
        center.Changed += (_, _) => nested ??= notice.Report("重畫通知島");

        Assert.True(notice.Report("建立結構預覽"));
        Assert.False(nested);
        Assert.Single(center.RecentFailures);
    }
}
