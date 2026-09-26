using System;
using SqlAssist.Core.Search;
using Xunit;

namespace SqlAssist.Core.Tests.Search;

public sealed class SearchEtaTests
{
    [Fact]
    public void 跑不到兩秒或不到一成時不說()
    {
        var eta = new SearchEta();

        Assert.Null(eta.Observe(TimeSpan.FromSeconds(1), 0.5));
        Assert.Null(eta.Observe(TimeSpan.FromSeconds(10), 0.05));
    }

    [Fact]
    public void 照實際速率外推並進位到五秒()
    {
        var eta = new SearchEta();

        // 四秒走了兩成：剩下八成約十六秒，進位成二十秒。
        Assert.Equal(TimeSpan.FromSeconds(20), eta.Observe(TimeSpan.FromSeconds(4), 0.2));
    }

    /// <summary>新的一次估計只拉一部分過去；直接換成最新那一次，數字會跟著每一個慢的資料庫跳。</summary>
    [Fact]
    public void 估計平滑收斂而不是直接跳到最新一次()
    {
        var eta = new SearchEta();

        eta.Observe(TimeSpan.FromSeconds(4), 0.2);

        // 單看這一次是剩 30 秒；平滑後介於 16 與 30 之間，進位成 25 秒。
        Assert.Equal(TimeSpan.FromSeconds(25), eta.Observe(TimeSpan.FromSeconds(10), 0.25));
    }

    [Fact]
    public void 進度倒退時整份重來()
    {
        var eta = new SearchEta();

        eta.Observe(TimeSpan.FromSeconds(4), 0.5);

        // 又宣告了新目標，分母變了；舊的速率不代表新的分母。
        Assert.Equal(TimeSpan.FromSeconds(30), eta.Observe(TimeSpan.FromSeconds(10), 0.25));
    }

    [Fact]
    public void 走完時不說()
    {
        Assert.Null(new SearchEta().Observe(TimeSpan.FromSeconds(10), 1));
    }
}
