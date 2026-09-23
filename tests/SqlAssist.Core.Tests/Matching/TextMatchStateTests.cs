using System;
using SqlAssist.Core.Matching;
using Xunit;

namespace SqlAssist.Core.Tests.Matching;

public sealed class TextMatchStateTests
{
    [Theory]
    [InlineData(TextMatchOptions.None, "0|0")]
    [InlineData(TextMatchOptions.MatchCasing, "1|0")]
    [InlineData(TextMatchOptions.WholeWord, "0|1")]
    [InlineData(TextMatchOptions.MatchCasing | TextMatchOptions.WholeWord, "1|1")]
    public void 格式寫出去讀得回來(TextMatchOptions options, string token)
    {
        Assert.Equal(token, TextMatchState.Format(options));
        Assert.True(TextMatchState.TryParse(token, out var parsed));
        Assert.Equal(options, parsed);
    }

    /// <summary>半套還原與「使用者上次真的這樣設」在畫面上一模一樣，所以認不得就整組不算。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1|")]
    [InlineData("2|0")]
    [InlineData("1|0|0")]
    [InlineData("1,0")]
    public void 認不得的字串整組不算(string? token)
    {
        Assert.False(TextMatchState.TryParse(token, out var parsed));
        Assert.Equal(TextMatchOptions.None, parsed);
    }

    [Fact]
    public void 認不得的位元直接拒絕()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextMatchState.Require((TextMatchOptions)4, "options"));
        Assert.Equal(TextMatchOptions.WholeWord, TextMatchState.Require(TextMatchOptions.WholeWord, "options"));
    }
}
