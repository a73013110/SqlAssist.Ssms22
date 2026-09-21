using SqlAssist.Core.Matching;
using Xunit;

namespace SqlAssist.Core.Tests.Matching;

public sealed class MatchCursorTests
{
    private static MatchCursor Three() =>
        new(new[] { new MatchSpan(0, 3), new MatchSpan(10, 3), new MatchSpan(20, 3) });

    [Fact]
    public void 空的游標按不動也不擲例外()
    {
        var cursor = MatchCursor.Empty;

        Assert.True(cursor.IsEmpty);
        Assert.Equal(0, cursor.Count);
        Assert.Equal(-1, cursor.Index);
        Assert.Equal(0, cursor.Position);
        Assert.Null(cursor.Current);
        Assert.False(cursor.MoveNext());
        Assert.False(cursor.MovePrevious());
        Assert.False(cursor.MoveTo(0));
    }

    [Fact]
    public void 起始停在第一個()
    {
        var cursor = Three();

        Assert.Equal(0, cursor.Index);
        Assert.Equal(1, cursor.Position);
        Assert.Equal(new MatchSpan(0, 3), cursor.Current);
    }

    /// <summary>
    /// 走到底回到第一個，走到頭回到最後一個。
    /// </summary>
    /// <remarks>
    /// 夾住而不環繞的症狀是使用者按到最後一個之後，「下一個」那顆按鈕看起來壞了。
    /// </remarks>
    [Fact]
    public void 走到底會環繞()
    {
        var cursor = Three();

        Assert.True(cursor.MoveNext());
        Assert.True(cursor.MoveNext());
        Assert.Equal(3, cursor.Position);

        Assert.True(cursor.MoveNext());
        Assert.Equal(1, cursor.Position);

        Assert.True(cursor.MovePrevious());
        Assert.Equal(3, cursor.Position);
    }

    /// <summary>只有一段時環繞等於原地不動，所以回 false——呼叫端不會為了同一個位置再捲一次。</summary>
    [Fact]
    public void 只有一段時移動不算變過()
    {
        var cursor = new MatchCursor(new[] { new MatchSpan(4, 2) });

        Assert.False(cursor.MoveNext());
        Assert.False(cursor.MovePrevious());
        Assert.Equal(1, cursor.Position);
    }

    [Fact]
    public void 跳到指定位置超出範圍會環繞()
    {
        var cursor = Three();

        Assert.True(cursor.MoveTo(4));
        Assert.Equal(2, cursor.Position);

        Assert.True(cursor.MoveTo(-1));
        Assert.Equal(3, cursor.Position);

        Assert.False(cursor.MoveTo(2));
    }

    /// <summary>起始位置超出範圍時夾回去，不擲例外：算出幾段與要停在第幾個是兩輪各自的結果。</summary>
    [Fact]
    public void 起始位置超出範圍會夾回去()
    {
        Assert.Equal(3, new MatchCursor(Three().Spans, 9).Position);
        Assert.Equal(1, new MatchCursor(Three().Spans, -4).Position);
    }
}
