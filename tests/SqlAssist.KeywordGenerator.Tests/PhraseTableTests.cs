using Xunit;

namespace SqlAssist.KeywordGenerator.Tests;

/// <summary>片語表的順序就是產物的順序：覆蓋不改位置，探測文字的索引跟著表的順序。</summary>
public sealed class PhraseTableTests
{
    [Fact]
    public void 同一個鍵再寫一次是原地覆蓋()
    {
        var table = new PhraseTable();
        table.Set("A\tX", Phrase("X", "x "));
        table.Set("A\tY", Phrase("Y", "y "));
        table.Set("a\tx", Phrase("X", "z "));

        Assert.Equal(["A\tX", "A\tY"], table.Keys);
        Assert.Equal("z ", table.Values[0].Probe);
        Assert.Empty(table.WithProbe("x "));
        Assert.Equal("A\tX", table.FirstKeyWithProbe("Z "));
    }

    [Fact]
    public void 探測文字相同的片語照表的順序()
    {
        var table = new PhraseTable();
        table.Set("A\tX", Phrase("X", "p "));
        table.Set("A\tY", Phrase("Y", "q "));
        table.Set("B\tZ", Phrase("Z", "p "));
        table.Set("A\tY", Phrase("Y", "p "));

        Assert.Equal(["X", "Y", "Z"], [.. System.Linq.Enumerable.Select(table.WithProbe("p "), phrase => phrase.Pattern)]);
        Assert.Equal("A\tX", table.FirstKeyWithProbe("p "));
        Assert.Null(table.FirstKeyWithProbe("q "));
    }

    private static ProbedPhrase Phrase(string pattern, string probe)
    {
        return new ProbedPhrase(pattern, "A", probe, []);
    }
}
