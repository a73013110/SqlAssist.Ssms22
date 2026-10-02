using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace SqlAssist.KeywordGenerator.Tests;

/// <summary>存進快取的事實：拼接詞元的剖析要與整段剖析說同一句話，否則快取裡的每一筆都可疑。</summary>
public sealed class ProbeFactsTests
{
    [Theory]
    [InlineData("SELECT ", "TOP", " 1 a FROM t")]
    [InlineData("SELECT * FROM t ORDER BY a ", "OFFSET", " 10 ROWS")]
    [InlineData("SELECT a FROM t ", "GO", " x")]
    [InlineData("MERGE t USING s ON 1 = 1 WHEN MATCHED THEN ", "DELETE", " OUTPUT")]
    [InlineData("SELECT ", "Lib_Reader", " FROM ) ")]
    public void 拼接與整段剖析的拒收位置相同(string prefix, string word, string continuation)
    {
        var facts = Create();

        Assert.Equal(facts.FirstRejection(prefix + word + continuation), facts.FirstRejection(prefix, word, continuation));
        Assert.Equal(facts.IsComplete(prefix + word + continuation), facts.IsComplete(prefix, word, continuation));
    }

    [Fact]
    public void MERGE少了分號_補上分號仍跳過一段才算整段過不了()
    {
        var facts = Create();
        const string prefix = "MERGE t USING s ON 1 = 1 WHEN MATCHED THEN DELETE ";

        Assert.Equal(0L, facts.AcceptedFacts(prefix, "OUTPUT", " x") >> 32);
        Assert.NotEqual(0L, facts.AcceptedFacts(prefix, "Lib_Reader", " x") >> 32);
    }

    [Fact]
    public void 字的分類_拒收落在字之前之中或續尾()
    {
        var packed = Create().Classes("SELECT * FROM t ", new[] { "WHERE", "ORDER", "Lib_Reader" }, " a = 1");

        Assert.Equal(ProbeFacts.Whole, packed[0] & 3);
        Assert.Equal(ProbeFacts.InContinuation, (packed[0] >> 2) & 3);
    }

    [Fact]
    public void 快取作廢條件取自嵌入的區段()
    {
        var hash = ProbeFacts.SourceHash();

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, ProbeFacts.SourceHash());
    }

    private static ProbeFacts Create()
    {
        return new ProbeFacts(typeof(TSql170Parser), KeywordProberTests.Rejecting);
    }
}
