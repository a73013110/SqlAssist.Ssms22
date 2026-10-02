using System;
using System.Collections.Generic;
using System.IO;
using SqlAssist.KeywordGenerator.Data;
using Xunit;

namespace SqlAssist.KeywordGenerator.Tests;

/// <summary>第四階段：探測文字怎麼組、名稱與值代入哪一種寫法、片語裡的每一個字怎麼補回前面那段。</summary>
public sealed class PhraseExplorerTests : IDisposable
{
    private static readonly string[] Pool = ["ALTER", "HIGH", "LOW", "NOCOUNT", "OFF", "ON", "OR", "PROCEDURE", "SELECT", "TABLE"];

    private static readonly Dictionary<string, string[]> Templates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["StatementStart"] = ["", "SELECT 1; "],
    };

    private readonly string _cachePath = Path.Combine(Path.GetTempPath(), "SqlAssist.KeywordGenerator.Tests." + Guid.NewGuid() + ".cache");

    public void Dispose()
    {
        File.Delete(_cachePath);
    }

    [Theory]
    [InlineData("", "ALTER INDEX {name} ON {name}", null, null, null, "ALTER INDEX t ON t ")]
    [InlineData("", "EXEC ... WITH ,*", null, "p", null, "EXEC p WITH ")]
    [InlineData("", "RAISERROR () WITH ,*", "('x', 16, 1)", null, null, "RAISERROR ('x', 16, 1) WITH ")]
    [InlineData("SELECT ", "PERIOD FOR SYSTEM_TIME ()", null, null, null, "SELECT PERIOD FOR SYSTEM_TIME (a) ")]
    [InlineData("SELECT * FROM ", "OPENROWSET (*", null, null, null, "SELECT * FROM OPENROWSET (")]
    [InlineData("ALTER TABLE t SWITCH TO t WITH (", "WAIT_AT_LOW_PRIORITY (* ABORT_AFTER_WAIT =", null, null, "MAX_DURATION = 1 MINUTES, ",
        "ALTER TABLE t SWITCH TO t WITH (WAIT_AT_LOW_PRIORITY (MAX_DURATION = 1 MINUTES, ABORT_AFTER_WAIT = ")]
    [InlineData("CREATE SEQUENCE t ", "", null, null, null, "CREATE SEQUENCE t ")]
    public void 探測文字代入名稱括號與Gap(string lead, string pattern, string? group, string? gap, string? items, string expected)
    {
        Assert.Equal(expected, Create().ProbeText(lead, pattern, group, gap, items: items));
    }

    [Fact]
    public void 值的寫法取剖析器收的第一種()
    {
        var explorer = Create();

        Assert.Equal("1", explorer.SelectValue("FETCH ABSOLUTE "));
        Assert.Equal("'x'", explorer.SelectValue("CREATE LOGIN l WITH PASSWORD = "));
        Assert.Null(explorer.SelectValue("ALTER INDEX "));
        Assert.Equal("FETCH ABSOLUTE 1 FROM ", explorer.ProbeText(string.Empty, "FETCH ABSOLUTE {value} FROM"));
    }

    [Fact]
    public void 只收兩段名稱的格子代入兩段()
    {
        var explorer = Create();

        Assert.Equal("t.t", explorer.SelectName("CREATE EVENT SESSION s ON SERVER ADD EVENT ", ","));
        Assert.Equal("t", explorer.SelectName("ALTER INDEX ", "ON"));
    }

    [Fact]
    public void 等號之後代入那一格列得出的第一個字()
    {
        var explorer = Create();
        const string probe = "CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM = ";

        Assert.Equal("t", explorer.SelectName(probe, null));

        explorer.Explore([new("CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM =") { Values = ["AES_128", "AES_256"], Closed = true }]);

        Assert.Equal("AES_128", explorer.SelectName(probe, null));
        Assert.Equal(probe + "AES_128 ENCRYPTION ", explorer.ProbeText(string.Empty, "CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM = {name} ENCRYPTION"));
    }

    [Fact]
    public void 手寫的值補進探到的字之後()
    {
        var explorer = Create();

        explorer.Explore([new("SET DEADLOCK_PRIORITY") { Values = ["LOW", "NORMAL", "HIGH"], Closed = true }]);
        var phrase = explorer.Phrases[ProbedPhrase.Key("StatementStart", "SET DEADLOCK_PRIORITY")];

        Assert.Equal("SET DEADLOCK_PRIORITY ", phrase.Probe);
        Assert.Equal(["LOW", "NORMAL", "HIGH"], phrase.Words);
        Assert.True(phrase.Closed);
    }

    [Fact]
    public void 封閉的格子只有接得上的字()
    {
        var explorer = Create();

        explorer.Explore([new("SET NOCOUNT")]);
        var phrase = explorer.Phrases[ProbedPhrase.Key("StatementStart", "SET NOCOUNT")];

        Assert.Equal(["OFF", "ON"], phrase.Words);
        Assert.True(phrase.Closed);
        Assert.False(phrase.TakesOperand);
    }

    [Fact]
    public void 整段剖析得過的片語把每一個字補進前面那段()
    {
        var explorer = Create();
        PhraseDeclaration[] declarations = [new("CREATE"), new("CREATE OR ALTER")];

        explorer.Explore([declarations[0]]);
        explorer.AddEvidence(declarations);

        Assert.Contains("OR", explorer.Phrases[ProbedPhrase.Key("StatementStart", "CREATE")].Words);
        Assert.Equal(["ALTER"], explorer.Phrases[ProbedPhrase.Key("StatementStart", "CREATE OR")].Words);
    }

    [Fact]
    public void 整段剖析不過的片語不當證據()
    {
        var explorer = Create();

        Assert.Throws<InvalidOperationException>(() => explorer.AddEvidence([new("CREATE OR NOTHING")]));
    }

    [Fact]
    public void 手寫值剖析不過就中止()
    {
        var explorer = Create();

        var exception = Assert.Throws<InvalidOperationException>(() => explorer.Explore([new("SET NOCOUNT") { Values = ["SELECT FROM"] }]));
        Assert.Contains("SELECT FROM", exception.Message);
    }

    private PhraseExplorer Create()
    {
        var prober = new KeywordProber(KeywordProberTests.Rejecting, _cachePath, loadCache: false);
        return new PhraseExplorer(prober, Pool, ["ALTER", "OR", "PROCEDURE", "SELECT", "TABLE", "ON", "OFF"], Pool,
            new Dictionary<string, List<string>>(), Templates, Continuations.Phrases);
    }
}
