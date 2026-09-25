using System.Linq;
using SqlAssist.Core.Snippets;
using Xunit;
using SqlAssist.Core.Localization;

namespace SqlAssist.Core.Tests.Snippets;

public sealed class SqlSnippetOverrideTests
{
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

    [Fact]
    public void 使用者自訂的內建片段在英文介面仍然蓋過內建值()
    {
        var custom = new SqlSnippet("ssf", "SELECT * FROM $end$;", title: "我的查詢", id: "builtin.ssf");
        var document = new SqlSnippetDocument(2, new[] { new SqlSnippetOverride(custom.Id, false, custom) });

        using (SqlText.Use(SqlLanguage.Find("en")!))
        {
            var loaded = SqlSnippetMerger.Merge(SqlSnippetDefaults.Current, document);

            Assert.True(loaded.Library.TryGet("ssf", out var effective));
            Assert.Equal("我的查詢", effective.Title);
        }
    }

    /// <remarks>
    /// 管理介面在繁中載入、換成英文後才存檔時，沒改過的內建項目仍是繁中的文字；
    /// 只和目前語言比的話，49 筆全部會被當成自訂寫進使用者檔。
    /// </remarks>
    [Fact]
    public void 與任何語言的內建值相同都不寫成自訂紀錄()
    {
        var loaded = SqlSnippetMerger.Merge(SqlSnippetDefaults.Current, SqlSnippetDocument.Empty);

        using (SqlText.Use(SqlLanguage.Find("en")!))
        {
            var document = SqlSnippetMerger.CreateOverrides(loaded.Entries, SqlSnippetDefaults.Current);

            Assert.Empty(document.Snippets);
        }
    }
}
