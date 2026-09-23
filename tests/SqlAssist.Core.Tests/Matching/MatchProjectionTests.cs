using System;
using System.Collections.Generic;
using SqlAssist.Core.Matching;
using Xunit;

namespace SqlAssist.Core.Tests.Matching;

public sealed class MatchProjectionTests
{
    [Fact]
    public void 區段跟著位移平移()
    {
        var spans = new[] { new MatchSpan(0, 4), new MatchSpan(4, 2) };
        var shifted = MatchProjection.Shift(spans, 10, fragmentLength: 6, textLength: 40);

        Assert.Equal(new[] { new MatchSpan(10, 4), new MatchSpan(14, 2) }, shifted);
    }

    /// <remarks>
    /// 只放棄超出的那一段等於把其餘幾段畫在對不起來的位置上；那看起來像是比對錯了。
    /// </remarks>
    [Fact]
    public void 任何一段超出片段就整組放棄()
    {
        var spans = new[] { new MatchSpan(0, 4), new MatchSpan(4, 9) };

        Assert.Empty(MatchProjection.Shift(spans, 10, fragmentLength: 6, textLength: 40));
    }

    [Fact]
    public void 平移之後超出整份文字也整組放棄()
    {
        var spans = new[] { new MatchSpan(0, 4) };

        Assert.Empty(MatchProjection.Shift(spans, 10, fragmentLength: 4, textLength: 12));
    }

    [Fact]
    public void 沒有區段時交出空的清單()
    {
        Assert.Empty(MatchProjection.Shift(Array.Empty<MatchSpan>(), 0, 0, 10));
    }

    /// <remarks>
    /// 清單那一端就是這樣把名稱命中的高亮換到限定名稱上的：片段是名稱本體，整份文字是限定名稱。
    /// </remarks>
    [Fact]
    public void 名稱命中換算到限定名稱上()
    {
        const string title = "[dbo].[Loan]";
        IReadOnlyList<MatchSpan> spans = new[] { new MatchSpan(0, 2) };

        var offset = new TextMatcher("Loan", TextMatchOptions.MatchCasing).LastIndexOf(title);

        Assert.Equal(7, offset);
        Assert.Equal(new[] { new MatchSpan(7, 2) }, MatchProjection.Shift(spans, offset, 4, title.Length));
    }
}
