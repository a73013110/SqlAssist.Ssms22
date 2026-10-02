using Xunit;

namespace SqlAssist.KeywordGenerator.Tests;

/// <summary>探測完的收尾：唯一的接續併成一項，同結果的位置併成一個片語。</summary>
public sealed class PhraseMergingTests
{
    [Fact]
    public void 後面只接得了一個字的字與那個字併成一項()
    {
        var table = new PhraseTable();
        Add(table, "CREATE", ["ASYMMETRIC", "TABLE"]);
        Add(table, "CREATE ASYMMETRIC", ["KEY"]);
        Add(table, "CREATE ASYMMETRIC KEY", ["k"], takesOperand: true);

        PhraseMerging.ChainUniqueContinuations(table);

        Assert.Equal(["ASYMMETRIC KEY", "TABLE"], table[ProbedPhrase.Key("StatementStart", "CREATE")].Words);
        Assert.Equal(["KEY"], table[ProbedPhrase.Key("StatementStart", "CREATE ASYMMETRIC")].Words);
    }

    [Fact]
    public void 寫到那裡已經完整或不封閉的字不併()
    {
        var table = new PhraseTable();
        Add(table, "OPEN", ["SYMMETRIC", "MASTER"]);
        Add(table, "OPEN SYMMETRIC", ["KEY"], endsStatement: true);
        Add(table, "OPEN MASTER", ["KEY"], closed: false);

        PhraseMerging.ChainUniqueContinuations(table);

        Assert.Equal(["SYMMETRIC", "MASTER"], table[ProbedPhrase.Key("StatementStart", "OPEN")].Words);
    }

    [Fact]
    public void 唯一的接續一路往下併()
    {
        var table = new PhraseTable();
        Add(table, "ALTER MASTER KEY", ["ADD"]);
        Add(table, "ALTER MASTER KEY ADD", ["ENCRYPTION"]);
        Add(table, "ALTER MASTER KEY ADD ENCRYPTION", ["BY"]);
        Add(table, "ALTER MASTER KEY ADD ENCRYPTION BY", ["PASSWORD", "SERVICE"]);

        PhraseMerging.ChainUniqueContinuations(table);

        Assert.Equal(["ADD ENCRYPTION BY"], table[ProbedPhrase.Key("StatementStart", "ALTER MASTER KEY")].Words);
    }

    [Fact]
    public void 同一條尾巴探到相同結果的位置併成一個_結果不同的各自一個()
    {
        var phrases = new[]
        {
            Phrase("FOR", "SelectListTail", ["XML", "JSON"]),
            Phrase("FOR", "TableSourceTail", ["XML", "JSON", "SYSTEM_TIME"]),
            Phrase("FOR", "ExpressionTail", ["XML", "JSON"]),
        };

        var merged = PhraseMerging.MergePositions(phrases);

        Assert.Equal(2, merged.Count);
        Assert.Equal(["SelectListTail", "ExpressionTail"], merged[0].After);
        Assert.Equal(["TableSourceTail"], merged[1].After);
        Assert.Equal(["SelectListTail"], phrases[0].After);
    }

    [Fact]
    public void 封閉或收變數不同的不併()
    {
        var closed = Phrase("SET", "StatementStart", ["NOCOUNT"]);
        var open = Phrase("SET", "BlockStart", ["NOCOUNT"]);
        open.Closed = false;

        Assert.Equal(2, PhraseMerging.MergePositions([closed, open]).Count);
    }

    private static void Add(PhraseTable table, string pattern, string[] words, bool closed = true, bool endsStatement = false, bool takesOperand = false)
    {
        table.Set(ProbedPhrase.Key("StatementStart", pattern), new ProbedPhrase(pattern, "StatementStart", pattern + " ", words)
        {
            Closed = closed,
            EndsStatement = endsStatement,
            TakesOperand = takesOperand,
        });
    }

    private static ProbedPhrase Phrase(string pattern, string after, string[] words)
    {
        return new ProbedPhrase(pattern, after, pattern + " ", words) { Closed = true };
    }
}
