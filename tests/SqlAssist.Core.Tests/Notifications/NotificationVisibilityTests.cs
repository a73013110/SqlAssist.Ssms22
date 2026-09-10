using System;
using System.Linq;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Notifications;

public sealed class NotificationVisibilityTests
{
    [Theory]
    [InlineData(NotificationKind.Metadata, true)]
    [InlineData(NotificationKind.Completion, false)]
    [InlineData(NotificationKind.Analysis, false)]
    [InlineData(NotificationKind.Preview, false)]
    [InlineData(NotificationKind.Editing, true)]
    [InlineData(NotificationKind.Navigation, true)]
    [InlineData(NotificationKind.Results, true)]
    [InlineData(NotificationKind.Snippets, true)]
    [InlineData(NotificationKind.Settings, true)]
    [InlineData(NotificationKind.Package, false)]
    [InlineData(NotificationKind.Unclassified, true)]
    public void 預設按種類降噪且漏分類看得見(NotificationKind kind, bool visible)
    {
        var settings = new SqlAssistSettings();
        var running = Item(kind, NotificationOrigin.Ambient);
        Assert.Equal(kind, running.Kind);
        Assert.Equal(NotificationSeverity.Info, running.Severity);
        Assert.Equal(visible, NotificationVisibility.Includes(running, settings));
    }

    [Fact]
    public void 失敗與降級走獨立通道且總開關優先()
    {
        var settings = new SqlAssistSettings();
        // Analysis 的種類開關預設關閉，用它證明失敗與降級不看種類。
        var failed = Item(NotificationKind.Analysis, NotificationOrigin.Typing, NotificationLevel.Debug, NotificationStatus.Failed);
        var degraded = Item(NotificationKind.Analysis, NotificationOrigin.Typing, NotificationLevel.Debug, NotificationStatus.Degraded);
        Assert.Equal(NotificationSeverity.Error, failed.Severity);
        Assert.Equal(NotificationSeverity.Warning, degraded.Severity);
        Assert.True(NotificationVisibility.Includes(failed, settings));
        Assert.True(NotificationVisibility.Includes(degraded, settings));
        foreach (var off in new[]
        {
            new SqlAssistSettings { NotificationEnabled = false },
            new SqlAssistSettings { Enabled = false },
        })
        {
            Assert.False(NotificationVisibility.Includes(failed, off));
            Assert.False(NotificationVisibility.Includes(degraded, off));
        }
        // 兩個通道各自獨立：關掉失敗不影響降級，反之亦然。
        Assert.False(NotificationVisibility.Includes(failed, new SqlAssistSettings { NotificationFailures = false }));
        Assert.True(NotificationVisibility.Includes(degraded, new SqlAssistSettings { NotificationFailures = false }));
        Assert.False(NotificationVisibility.Includes(degraded, new SqlAssistSettings { NotificationDegraded = false }));
        Assert.True(NotificationVisibility.Includes(failed, new SqlAssistSettings { NotificationDegraded = false }));
    }

    [Fact]
    public void 取消不是錯誤也不是降級()
    {
        var canceled = Item(NotificationKind.Metadata, NotificationOrigin.Ambient, NotificationLevel.Info, NotificationStatus.Canceled);
        Assert.Equal(NotificationSeverity.Info, canceled.Severity);
        Assert.False(NotificationVisibility.Includes(canceled, Without(NotificationKind.Metadata)));
    }

    [Fact]
    public void 使用者觸發的工作跨得過門檻但跨不過種類開關()
    {
        // 詳細度調到最安靜、種類全開：使用者按下去的事不因為降噪而消失。
        var quiet = new SqlAssistSettings { NotificationVerbosity = NotificationVerbosity.Quiet, NotificationKinds = AllOn };
        // 再把種類開關關到底：關掉那一類就是連自己按的也不想再看到。
        var quietAndOff = new SqlAssistSettings { NotificationVerbosity = NotificationVerbosity.Quiet, NotificationKinds = AllOff };
        foreach (NotificationKind kind in Enum.GetValues(typeof(NotificationKind)))
        {
            Assert.True(NotificationVisibility.Includes(Item(kind, NotificationOrigin.User), quiet));
            Assert.False(NotificationVisibility.Includes(Item(kind, NotificationOrigin.User), quietAndOff));
            // 換成自動觸發就回到門檻管轄；Info 在精簡模式下一律讓位。
            Assert.False(NotificationVisibility.Includes(Item(kind, NotificationOrigin.Typing), quiet));
        }
    }

    [Fact]
    public void 每一個種類都關得掉()
    {
        // 十一格核取方塊每一格都要管得住自己那一類，不分誰觸發。
        foreach (var toggle in NotificationKindToggle.All)
        {
            Assert.NotEmpty(toggle.Moniker);
            var off = new SqlAssistSettings { NotificationKinds = NotificationKindSwitches.Defaults.With(toggle.Kind, false) };
            foreach (NotificationOrigin origin in Enum.GetValues(typeof(NotificationOrigin)))
                Assert.False(NotificationVisibility.Includes(Item(toggle.Kind, origin, NotificationLevel.Notice), off));
        }
    }

    [Fact]
    public void 兩軸互相獨立()
    {
        var settings = new SqlAssistSettings();
        foreach (NotificationOrigin origin in Enum.GetValues(typeof(NotificationOrigin)))
        {
            // 種類只回答「做什麼」：中繼資料查詢不因為誰觸發而換成別的種類。
            var item = Item(NotificationKind.Metadata, origin);
            Assert.Equal(NotificationKind.Metadata, item.Kind);
            Assert.Equal(origin, item.Origin);
            Assert.True(NotificationVisibility.Includes(item, settings));
            // 關掉「中繼資料載入」擋得住每一種來源；來源只管跨不跨得過降噪門檻。
            Assert.False(NotificationVisibility.Includes(item, Without(NotificationKind.Metadata)));
        }
    }

    [Theory]
    [InlineData(NotificationLevel.Trace, NotificationVerbosity.Verbose, false)]
    [InlineData(NotificationLevel.Trace, NotificationVerbosity.Normal, false)]
    [InlineData(NotificationLevel.Debug, NotificationVerbosity.Quiet, false)]
    [InlineData(NotificationLevel.Debug, NotificationVerbosity.Normal, false)]
    [InlineData(NotificationLevel.Debug, NotificationVerbosity.Verbose, true)]
    [InlineData(NotificationLevel.Info, NotificationVerbosity.Quiet, false)]
    [InlineData(NotificationLevel.Info, NotificationVerbosity.Normal, true)]
    [InlineData(NotificationLevel.Info, NotificationVerbosity.Verbose, true)]
    [InlineData(NotificationLevel.Notice, NotificationVerbosity.Quiet, true)]
    // 「全部」把門檻降到 Trace，四階全部上畫面。
    [InlineData(NotificationLevel.Trace, NotificationVerbosity.All, true)]
    [InlineData(NotificationLevel.Debug, NotificationVerbosity.All, true)]
    [InlineData(NotificationLevel.Info, NotificationVerbosity.All, true)]
    [InlineData(NotificationLevel.Notice, NotificationVerbosity.All, true)]
    public void 詳細度門檻(NotificationLevel level, NotificationVerbosity verbosity, bool visible)
    {
        // Metadata 的開關預設開著，門檻才是唯一的變因。
        var settings = new SqlAssistSettings { NotificationVerbosity = verbosity };
        Assert.Equal(visible, NotificationVisibility.Includes(Item(NotificationKind.Metadata, NotificationOrigin.Typing, level), settings));
    }

    [Fact]
    public void Notice跨過詳細度門檻但Trace要選全部()
    {
        var settings = new SqlAssistSettings { NotificationVerbosity = NotificationVerbosity.Quiet };
        // 精簡模式下 Info 會讓位，Notice 不會。
        Assert.False(NotificationVisibility.Includes(Item(NotificationKind.Metadata, NotificationOrigin.Ambient, NotificationLevel.Info), settings));
        Assert.True(NotificationVisibility.Includes(Item(NotificationKind.Metadata, NotificationOrigin.Ambient, NotificationLevel.Notice), settings));
        // 但 Notice 跨不過種類開關。
        Assert.False(NotificationVisibility.Includes(Item(NotificationKind.Metadata, NotificationOrigin.Ambient, NotificationLevel.Notice),
            Without(NotificationKind.Metadata)));
        settings = new SqlAssistSettings { NotificationVerbosity = NotificationVerbosity.Verbose };
        // 「詳細」還擋得住 Trace，連使用者剛觸發的也一樣；要看見它得選「全部」。
        Assert.False(NotificationVisibility.Includes(Item(NotificationKind.Metadata, NotificationOrigin.User, NotificationLevel.Trace), settings));
        Assert.Equal(NotificationLevel.Notice, NotificationVisibility.Threshold(NotificationVerbosity.Quiet));
        Assert.Equal(NotificationLevel.Debug, NotificationVisibility.Threshold(NotificationVerbosity.Verbose));
        Assert.Equal(NotificationLevel.Trace, NotificationVisibility.Threshold(NotificationVerbosity.All));
    }

    [Fact]
    public void 每個種類都在集中表裡且預設值與設定一致()
    {
        var defaults = new SqlAssistSettings();
        foreach (NotificationKind kind in Enum.GetValues(typeof(NotificationKind)))
        {
            var toggle = NotificationKindToggle.For(kind);
            Assert.Equal(kind, toggle.Kind);
            Assert.NotEmpty(toggle.Title);
            Assert.NotEmpty(toggle.Moniker);
            Assert.Equal(toggle.EnabledByDefault, defaults.NotificationKinds[kind]);
        }
        // 遮罩是 32 位元的；種類多到滿出來時位元會安靜地互相覆蓋。
        Assert.True(Enum.GetValues(typeof(NotificationKind)).Length <= 31);
        Assert.Equal(Enum.GetValues(typeof(NotificationKind)).Length, NotificationKindToggle.All.Count);
        Assert.All(NotificationKindToggle.All, x => Assert.Contains(x.Moniker, SqlAssistMonikers.All));
    }

    [Fact]
    public void 合併的巢狀查詢沿用父工作的三軸()
    {
        var center = new NotificationCenter();
        var logged = 0;
        center.Completed += item =>
        {
            logged++;
            Assert.Equal(NotificationKind.Editing, item.Kind);
            Assert.Equal(NotificationOrigin.User, item.Origin);
            Assert.Equal(NotificationStatus.Succeeded, item.Status);
        };
        using (center.Begin("展開語句樣板", NotificationKind.Editing, NotificationOrigin.User,
                   NotificationLevel.Info))
            using (center.Begin("載入欄位與定義", NotificationKind.Metadata, NotificationOrigin.Typing,
                NotificationLevel.Info, joinParent: true)) { }
        var item = Assert.Single(center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue));
        Assert.Equal(NotificationKind.Editing, item.Kind);
        Assert.Equal(NotificationOrigin.User, item.Origin);
        Assert.Equal(1, logged);
    }

    /// <summary>十一個種類全部關掉。</summary>
    private static NotificationKindSwitches AllOff => Every(false);

    /// <summary>十一個種類全部打開；預設關著的高頻種類也算進來。</summary>
    private static NotificationKindSwitches AllOn => Every(true);

    private static NotificationKindSwitches Every(bool enabled)
    {
        var switches = NotificationKindSwitches.Defaults;
        foreach (var toggle in NotificationKindToggle.All)
            switches = switches.With(toggle.Kind, enabled);
        return switches;
    }

    private static SqlAssistSettings Without(NotificationKind kind) =>
        new() { NotificationKinds = NotificationKindSwitches.Defaults.With(kind, false) };

    private static NotificationItem Item(NotificationKind kind, NotificationOrigin origin,
        NotificationLevel level = NotificationLevel.Info, NotificationStatus status = NotificationStatus.Running)
    {
        var center = new NotificationCenter();
        var scope = center.Begin("載入欄位與定義", kind, origin, level, "dbo.Loan", "LibArchive");
        scope.Report("安全進度");
        if (status == NotificationStatus.Failed) scope.Fail();
        if (status == NotificationStatus.Degraded) scope.Degrade();
        if (status == NotificationStatus.Canceled) scope.Cancel();
        if (status != NotificationStatus.Running) scope.Dispose();
        return Assert.Single(center.Snapshot(TimeSpan.MaxValue, TimeSpan.MaxValue));
    }
}
