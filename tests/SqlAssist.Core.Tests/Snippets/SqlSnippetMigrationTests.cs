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
        Assert.Equal(43, SqlSnippetMerger.Merge(SqlSnippetDefaults.Current, migrated).Library.Count);
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
        var configuration = SqlSnippetMerger.Merge(SqlSnippetDefaults.Current, SqlSnippetDocument.Empty);
        var document = SqlSnippetMerger.CreateOverrides(configuration.Entries, SqlSnippetDefaults.Current);

        Assert.Empty(document.Snippets);
    }

    /// <remarks>
    /// 遮住是當下的計算結果，不是使用者的決定。寫成停用紀錄的話，使用者之後把
    /// 撞名的自訂片段改名，內建那筆也再回不來——而且沒有任何徵兆。
    /// </remarks>
    [Fact]
    public void 被同捷徑遮住的內建片段存檔後不會變成停用紀錄()
    {
        var custom = new SqlSnippet("ssf", "SELECT 1$end$;", id: "user.ssf");
        var loaded = SqlSnippetMerger.Merge(
            SqlSnippetDefaults.Current,
            new SqlSnippetDocument(2, new[] { new SqlSnippetOverride(custom.Id, false, custom) }));

        var shadowed = loaded.Entries.Single(item => item.Snippet.Id == "builtin.ssf");
        Assert.True(shadowed.IsShadowed);
        Assert.False(shadowed.IsDisabled);

        var saved = SqlSnippetMerger.CreateOverrides(loaded.Entries, SqlSnippetDefaults.Current);

        Assert.DoesNotContain(saved.Snippets, record => record.Id == "builtin.ssf");

        // 把撞名的自訂片段改名之後，內建那筆自己回來。
        var renamed = saved.Snippets
            .Select(record => record.Id == custom.Id
                ? new SqlSnippetOverride(
                    record.Id,
                    false,
                    new SqlSnippet("mine", record.Snippet!.Code, id: record.Id))
                : record)
            .ToArray();

        Assert.True(SqlSnippetMerger
            .Merge(SqlSnippetDefaults.Current, new SqlSnippetDocument(2, renamed))
            .Library
            .TryGet("ssf", out var restored));
        Assert.Equal("builtin.ssf", restored.Id);
    }

    [Fact]
    public void 使用者主動停用的內建片段才寫成停用紀錄()
    {
        var loaded = SqlSnippetMerger.Merge(SqlSnippetDefaults.Current, SqlSnippetDocument.Empty);
        var entries = loaded.Entries
            .Select(entry => entry.Snippet.Id == "builtin.dt"
                ? new SqlSnippetConfigurationEntry(entry.Snippet, true, true, isDisabled: true)
                : entry)
            .ToArray();

        var record = Assert.Single(SqlSnippetMerger.CreateOverrides(entries, SqlSnippetDefaults.Current).Snippets);

        Assert.Equal("builtin.dt", record.Id);
        Assert.True(record.Disabled);
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

        var builtIn = merged.Entries.Single(item => item.Snippet.Id == "builtin.ssf");
        Assert.True(builtIn.IsShadowed);
        Assert.False(builtIn.IsEffective);
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
