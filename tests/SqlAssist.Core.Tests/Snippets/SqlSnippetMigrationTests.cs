using System.Linq;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Snippets;

public sealed class SqlSnippetMigrationTests
{
    [Fact]
    public void 原封不動的v1預設不產生override()
    {
        var migrated = SqlSnippetMerger.MigrateVersion1(ReadV1(Ssf, Ap, Af), SqlSnippetDefaults.Current);

        Assert.Empty(migrated.Snippets);
        Assert.Equal(40, SqlSnippetMerger.Merge(SqlSnippetDefaults.Current, migrated).Library.Count);
    }

    [Fact]
    public void 改過ssf只產生一筆override且維持caret模式()
    {
        var changed = Ssf.Replace("SELECT * FROM ", "SELECT TOP (10) * FROM ");
        var migrated = SqlSnippetMerger.MigrateVersion1(ReadV1(changed, Ap, Af), SqlSnippetDefaults.Current);

        var record = Assert.Single(migrated.Snippets);
        Assert.Equal("builtin.ssf", record.Id);
        Assert.False(record.Disabled);
        Assert.Equal(SqlSnippetExpansionMode.Caret, record.Snippet!.ExpansionMode);
    }

    [Fact]
    public void v1刪掉af會產生停用紀錄()
    {
        var migrated = SqlSnippetMerger.MigrateVersion1(ReadV1(Ssf, Ap), SqlSnippetDefaults.Current);

        var record = Assert.Single(migrated.Snippets);
        Assert.Equal("builtin.af", record.Id);
        Assert.True(record.Disabled);
    }

    [Fact]
    public void v1自訂捷徑後來成為內建時轉成該內建項目的override()
    {
        const string join = """
            {
              "shortcut": "ij",
              "title": "我的 INNER JOIN",
              "code": "INNER JOIN "
            }
            """;
        var migrated = SqlSnippetMerger.MigrateVersion1(ReadV1(Ssf, Ap, Af, join), SqlSnippetDefaults.Current);

        var record = Assert.Single(migrated.Snippets);
        Assert.Equal("builtin.ij", record.Id);
        Assert.Equal(SqlSnippetExpansionMode.Caret, record.Snippet!.ExpansionMode);
        Assert.Equal("我的 INNER JOIN", record.Snippet.Title);
    }

    [Fact]
    public void 沒改預設時v2檔案沒有任何紀錄()
    {
        var document = SqlSnippetMerger.CreateOverrides(SqlSnippetDefaults.Current, SqlSnippetDefaults.Current);

        Assert.Empty(document.Snippets);
    }

    [Fact]
    public void 自訂項目在合併後勝過撞捷徑的內建項目()
    {
        var custom = new SqlSnippet(
            "ssf",
            "SELECT 1$end$;",
            id: "user.ssf");
        var document = new SqlSnippetDocument(2, new[]
        {
            new SqlSnippetOverride(custom.Id, false, custom)
        });

        var merged = SqlSnippetMerger.Merge(SqlSnippetDefaults.Current, document);

        Assert.True(merged.Library.TryGet("ssf", out var winner));
        Assert.Equal("user.ssf", winner.Id);
        Assert.True(merged.Entries.Single(item => item.Snippet.Id == "builtin.ssf").IsDisabled);
    }

    private static SqlSnippetDocument ReadV1(params string[] snippets)
    {
        var body = string.Join(",", snippets);
        return SqlSnippetSerializer.DeserializeDocument($$"""
            {
              "version": 1,
              "snippets": [{{body}}]
            }
            """);
    }

    private const string Ssf = """
        {
          "shortcut": "ssf",
          "title": "SELECT * FROM",
          "description": "SELECT * FROM fragment",
          "triggerFollowUp": true,
          "code": "SELECT * FROM "
        }
        """;

    private const string Ap = """
        {
          "shortcut": "ap",
          "title": "ALTER PROCEDURE",
          "description": "ALTER PROCEDURE fragment",
          "triggerFollowUp": true,
          "code": "ALTER PROCEDURE "
        }
        """;

    private const string Af = """
        {
          "shortcut": "af",
          "title": "ALTER FUNCTION",
          "description": "ALTER FUNCTION fragment",
          "triggerFollowUp": true,
          "code": "ALTER FUNCTION "
        }
        """;
}
