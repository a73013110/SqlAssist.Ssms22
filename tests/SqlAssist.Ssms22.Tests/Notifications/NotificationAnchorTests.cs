using System;
using System.Collections.Generic;
using SqlAssist.Ssms22.Notifications;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Notifications;

public sealed class NotificationAnchorTests
{
    private const string Main = "主視窗";
    private const string Floating = "拆出去的框架";

    [Fact]
    public void 錨在使用者正在操作的框架()
    {
        // 查詢視窗拆出去之後回主視窗操作 SQL Search：通知跟著主視窗，不留在拆出去的框架上。
        Assert.Same(Main, Choose(active: Main, current: Floating));
        Assert.Same(Floating, Choose(active: Floating, current: Main));
    }

    [Fact]
    public void 焦點不在任何框架上時沿用目前的錨點()
    {
        // 別的程式、對話框或 WinForms 視窗在前面：不跳回主視窗，島嶼才不會來回重播。
        Assert.Same(Floating, Choose(active: null, current: Floating));
    }

    [Fact]
    public void 目前的錨點看不到時改用主視窗()
    {
        Assert.Same(Main, Choose(active: null, current: Floating, hidden: new[] { Floating }));
        Assert.Same(Main, Choose(active: null, current: null));
    }

    [Fact]
    public void 作用中框架看不到時不採用()
    {
        Assert.Same(Main, Choose(active: Floating, current: null, hidden: new[] { Floating }));
    }

    [Fact]
    public void 全都看不到時不錨()
    {
        Assert.Null(Choose(active: null, current: Floating, hidden: new[] { Floating, Main }));
    }

    private static string? Choose(string? active, string? current, string[]? hidden = null)
    {
        var invisible = new HashSet<string>(hidden ?? Array.Empty<string>());
        return NotificationAnchor.Choose(active, current, Main, window => !invisible.Contains(window));
    }
}
