using System;
using System.Collections.Generic;
using SqlAssist.Core.Matching;
using Xunit;

namespace SqlAssist.Core.Tests.Matching;

public sealed class MatchProjectionTests
{
    [Fact]
    public void 找得到片段第一次出現的位置()
    {
        Assert.Equal(9, MatchProjection.Find("SELECT * FROM Loan", "FROM", 0, MatchProjectionMode.None));
    }

    [Fact]
    public void 從指定位置之後才找()
    {
        const string text = "Loan JOIN Loan";
        Assert.Equal(10, MatchProjection.Find(text, "Loan", 1, MatchProjectionMode.None));
    }

    [Fact]
    public void 找不到時回傳負一()
    {
        Assert.Equal(-1, MatchProjection.Find("SELECT 1", "Loan", 0, MatchProjectionMode.None));
    }

    [Fact]
    public void 由後往前取最後一次出現()
    {
        const string title = "[Loan].[Loan]";
        Assert.Equal(8, MatchProjection.Find(title, "Loan", 0, MatchProjectionMode.FromEnd));
    }

    [Theory]
    [InlineData("[CopyNo] int")]
    [InlineData("CopyNo int")]
    [InlineData("a.CopyNo = 1")]
    public void 方括號與點算詞界(string text)
    {
        Assert.True(MatchProjection.Find(text, "CopyNo", 0, MatchProjectionMode.WholeWord) >= 0);
    }

    [Fact]
    public void 整個字不命中別的字裡面那一段()
    {
        Assert.Equal(-1, MatchProjection.Find("[CopyNo] int", "Copy", 0, MatchProjectionMode.WholeWord));
    }

    /// <remarks>
    /// 詞界不合的那一次之後可能緊接著一次合格的；停在第一次找到的位置就會整組放棄。
    /// </remarks>
    [Fact]
    public void 詞界不合時繼續往後找()
    {
        Assert.Equal(11, MatchProjection.Find("CopyNoTag [CopyNo]", "CopyNo", 0, MatchProjectionMode.WholeWord));
    }

    [Fact]
    public void 忽略大小寫只在指定時生效()
    {
        Assert.Equal(-1, MatchProjection.Find("[COPYNO]", "CopyNo", 0, MatchProjectionMode.WholeWord));
        Assert.Equal(
            1,
            MatchProjection.Find(
                "[COPYNO]", "CopyNo", 0, MatchProjectionMode.WholeWord | MatchProjectionMode.IgnoreCase));
    }

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

    [Fact]
    public void 空片段與越界起點都不算命中()
    {
        Assert.Equal(-1, MatchProjection.Find("SELECT 1", "", 0, MatchProjectionMode.None));
        Assert.Equal(-1, MatchProjection.Find("SELECT 1", "SELECT", 5, MatchProjectionMode.None));
        Assert.Equal(-1, MatchProjection.Find("SELECT", "SELECT 1", 0, MatchProjectionMode.None));
    }

    /// <remarks>
    /// 清單那一端就是這樣把名稱命中的高亮換到限定名稱上的：片段是名稱本體，整份文字是限定名稱。
    /// </remarks>
    [Fact]
    public void 名稱命中換算到限定名稱上()
    {
        const string title = "[dbo].[Loan]";
        IReadOnlyList<MatchSpan> spans = new[] { new MatchSpan(0, 2) };

        var offset = MatchProjection.Find(title, "Loan", 0, MatchProjectionMode.FromEnd);

        Assert.Equal(7, offset);
        Assert.Equal(new[] { new MatchSpan(7, 2) }, MatchProjection.Shift(spans, offset, 4, title.Length));
    }
}
