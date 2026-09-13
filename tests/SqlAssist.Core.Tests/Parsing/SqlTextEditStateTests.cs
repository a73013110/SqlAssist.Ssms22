using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

public sealed class SqlTextEditStateTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" SELECT 1;\r\n\n\r\t")]
    [InlineData("SELECT N'圖書館';\0")]
    public void EditingPreservesOriginalAndUndoClearsDirty(string sql)
    {
        var state = new SqlTextEditState(sql);
        Assert.Equal(sql, state.Text); Assert.False(state.IsModified);
        state.Text += " "; Assert.True(state.IsModified); Assert.Equal(sql, state.Original);
        state.Text = sql; Assert.False(state.IsModified);
    }
}
