using System;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class EditorFormatChangesTests
{
    [Fact]
    public void 格式重設與大小寫變更都會刷新()
    {
        Assert.True(EditorFormatChanges.Affects(Array.Empty<string>(), "Plain Text"));
        Assert.True(EditorFormatChanges.Affects(new[] { "plain text" }, "Plain Text"));
        Assert.True(EditorFormatChanges.Affects(new[] { "symbol" }, "keyword", "Symbol"));
    }

    [Fact]
    public void 自身回寫不使基礎配色重算且各呈現不互相刷新()
    {
        Assert.False(EditorFormatChanges.Affects(new[] { "SqlAssist.BlockKeyword", "SqlAssist.BlockRange" }, "Plain Text"));
        Assert.False(EditorFormatChanges.Affects(new[] { "SqlAssist.BlockRange" }, "SqlAssist.BlockKeyword", "SqlAssist.BlockSymbol"));
    }
}
