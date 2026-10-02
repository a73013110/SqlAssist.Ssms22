using System;
using System.Linq;
using Xunit;

namespace SqlAssist.KeywordGenerator.Tests;

/// <summary>語句說明當證據：多字的名稱，以及 DBCC 命令括號裡的字；寫法與命令名單對不上就中止。</summary>
public sealed class StatementDocsTests
{
    [Fact]
    public void 含空白的名稱與別名照文件順序()
    {
        var docs = StatementDocs.Parse("""
            { "docs": [
              { "kind": "statement", "name": "BEGIN TRANSACTION", "aliases": ["BEGIN TRAN", "COMMIT"] },
              { "kind": "function", "name": "NEXT VALUE FOR" },
              { "kind": "statement", "name": "WAITFOR" }
            ] }
            """);

        Assert.Equal(["BEGIN TRANSACTION", "BEGIN TRAN"], docs.StatementNames);
        Assert.Empty(docs.DbccArguments);
    }

    [Fact]
    public void DBCC每個命令第一組括號裡的大寫字_引號裡的不算()
    {
        var docs = StatementDocs.Parse(Dbcc(
            "DBCC CHECKIDENT ( table_name [ , { NORESEED | { RESEED [ , new_reseed_value ] } } ] )\nDBCC FREEPROCCACHE",
            """[["DBCC CHECKIDENT ('t', RESEED, 1)", "x"], ["DBCC SQLPERF (LOGSPACE) WITH NO_INFOMSGS", "x"]]""",
            "DBCC CHECKIDENT", "DBCC FREEPROCCACHE", "DBCC SQLPERF"));

        Assert.Equal(["CHECKIDENT", "FREEPROCCACHE", "SQLPERF"], docs.DbccArguments.Select(command => command.Key));
        Assert.Equal(["NORESEED", "RESEED"], docs.DbccArguments[0].Value);
        Assert.Empty(docs.DbccArguments[1].Value);
        Assert.Equal(["LOGSPACE"], docs.DbccArguments[2].Value);
    }

    [Theory]
    [InlineData("DBCC CHECKDB (d)", "DBCC CHECKALLOC", "別名卻沒有")]
    [InlineData("DBCC CHECKDB (d", "DBCC CHECKDB", "沒有關上")]
    [InlineData("DBCC CHECKDB (d)", "DBCC CHECKDB,DBCC SHRINKFILE", "SHRINKFILE")]
    public void 寫法與命令名單對不上就中止(string signature, string aliases, string problem)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => StatementDocs.Parse(Dbcc(signature, "[]", aliases.Split(','))));

        Assert.Contains(problem, exception.Message);
    }

    private static string Dbcc(string signature, string rows, params string[] aliases)
    {
        var aliasList = string.Join(", ", aliases.Select(alias => "\"" + alias + "\""));
        return $$"""
            { "docs": [ {
              "kind": "statement", "name": "DBCC", "aliases": [{{aliasList}}],
              "signature": "{{signature.Replace("\n", "\\n")}}",
              "references": [ { "title": "命令", "rows": {{rows}} } ]
            } ] }
            """;
    }
}
