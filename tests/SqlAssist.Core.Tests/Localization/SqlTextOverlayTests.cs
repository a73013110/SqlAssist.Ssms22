using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SqlAssist.Core.Json;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Localization;
using Xunit;

namespace SqlAssist.Core.Tests.Localization;

/// <summary>
/// 資料型文字的覆蓋檔：來源檔改了、覆蓋檔沒跟上的症狀是英文介面安靜地露出一句中文，
/// 或譯者弄丟了 <c>$surround$</c> 讓那個片段在英文下包夾不了——兩者都沒有執行期錯誤。
/// </summary>
public sealed class SqlTextOverlayTests
{
    private const string DocsResource = "SqlAssist.Core.Keywords.BuiltInDocs.json";
    private const string SnippetsResource = "SqlAssist.Core.Snippets.DefaultSnippets.json";

    private static readonly Regex Markers = new(
        @"\$[A-Za-z_][A-Za-z0-9_]*\$|\{[a-z][A-Za-z0-9]*(?:[,:][^}]*)?\}",
        RegexOptions.CultureInvariant);

    private static Assembly Core => typeof(SqlBuiltInDocCatalog).Assembly;

    public static TheoryData<string, string> Overlays()
    {
        var data = new TheoryData<string, string>();

        foreach (var language in SqlLanguage.All.Skip(1))
        {
            data.Add(DocsResource, language.Name);
            data.Add(SnippetsResource, language.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Overlays))]
    public void 覆蓋檔與來源的編號和欄位一致(string resource, string languageName)
    {
        var overlay = SqlTextOverlay.Load(Core, resource, SqlLanguage.Find(languageName)!);
        var source = SourceFields(resource);

        Assert.Null(overlay.Error);
        Assert.Equal(source.Keys.OrderBy(id => id, StringComparer.Ordinal), overlay.Ids.OrderBy(id => id, StringComparer.Ordinal));

        foreach (var pair in source)
        {
            Assert.Equal(
                pair.Value.Keys.OrderBy(field => field, StringComparer.Ordinal),
                overlay.FieldsOf(pair.Key).Keys.OrderBy(field => field, StringComparer.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(Overlays))]
    public void 譯文原樣保留佔位符與片段標記(string resource, string languageName)
    {
        var overlay = SqlTextOverlay.Load(Core, resource, SqlLanguage.Find(languageName)!);

        foreach (var pair in SourceFields(resource))
        {
            foreach (var field in pair.Value)
            {
                var translated = overlay.Apply(pair.Key, field.Key, field.Value);
                var where = $"{pair.Key} {field.Key}";

                Assert.Equal(MarkersOf(field.Value), MarkersOf(translated));
                Assert.False(ContainsCjk(translated), where);
                Assert.Equal(field.Value.Count(c => c == '\n'), translated.Count(c => c == '\n'));
            }
        }
    }

    [Fact]
    public void 來源語言不讀覆蓋檔()
    {
        var overlay = SqlTextOverlay.Load(Core, DocsResource, SqlLanguage.Source);

        Assert.Same(SqlTextOverlay.Empty, overlay);
        Assert.Equal("原文", overlay.Apply("CONVERT", "summary", "原文"));
    }

    [Fact]
    public void 覆蓋檔名稱在副檔名前插入語言()
    {
        Assert.Equal(
            "SqlAssist.Core.Keywords.BuiltInDocs.en.json",
            SqlTextOverlay.ResourceNameFor(DocsResource, SqlLanguage.Find("en")!));
    }

    [Fact]
    public void 沒有的編號與欄位退回來源()
    {
        var overlay = SqlTextOverlay.Parse("""{ "a": { "title": "Title", "count": 3 } }""");

        Assert.Equal("Title", overlay.Apply("a", "title", "標題"));
        Assert.Equal("說明", overlay.Apply("a", "description", "說明"));
        Assert.Equal("標題", overlay.Apply("b", "title", "標題"));
        Assert.Equal(new[] { "title" }, overlay.FieldsOf("a").Keys);
    }

    [Fact]
    public void 讀不到覆蓋檔時降級而不丟例外()
    {
        var overlay = SqlTextOverlay.Load(Core, "SqlAssist.Core.Missing.json", SqlLanguage.Find("en")!);

        Assert.NotNull(overlay.Error);
        Assert.Empty(overlay.Ids);
    }

    [Fact]
    public void 語言快取依語言各留一份()
    {
        var english = SqlLanguage.Find("en")!;
        var cache = new SqlLanguageCache<string>(language => language.Name + ":" + SqlText.Current.Name);

        Assert.Equal("zh-Hant:zh-Hant", cache.Current);
        Assert.Equal("en:en", cache.For(english));

        using (SqlText.Use(english))
        {
            Assert.Same(cache.For(english), cache.Current);
        }
    }

    /// <summary>來源裡每一筆要翻的欄位（含中文的文字欄），命名與各載入器相同。</summary>
    private static Dictionary<string, Dictionary<string, string>> SourceFields(string resource)
    {
        using var stream = Core.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var root = JsonReader.Parse(reader.ReadToEnd());
        var fields = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        void Add(string id, string field, string text)
        {
            if (!ContainsCjk(text))
            {
                return;
            }

            if (!fields.TryGetValue(id, out var entry))
            {
                fields[id] = entry = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            entry[field] = text;
        }

        if (resource == DocsResource)
        {
            foreach (var tableId in root["tables"].Names)
            {
                var table = root["tables"][tableId];
                var id = "tables." + tableId;
                Add(id, "title", table["title"].AsString());

                for (var column = 0; column < table["columns"].Items.Count; column++)
                {
                    Add(id, "columns." + column, table["columns"].Items[column].AsString());
                }

                for (var row = 0; row < table["rows"].Items.Count; row++)
                {
                    var cells = table["rows"].Items[row].Items;

                    for (var cell = 0; cell < cells.Count; cell++)
                    {
                        Add(id, "rows." + row + "." + cell, cells[cell].AsString());
                    }
                }
            }

            foreach (var doc in root["docs"].Items)
            {
                Add(doc["name"].AsString(), "summary", doc["summary"].AsString());
                Add(doc["name"].AsString(), "example", doc["example"].AsString());
            }
        }
        else
        {
            foreach (var snippet in root["snippets"].Items)
            {
                var id = snippet["id"].AsString();
                Add(id, "title", snippet["title"].AsString());
                Add(id, "description", snippet["description"].AsString());
                Add(id, "code", snippet["code"].AsString());

                foreach (var placeholder in snippet["placeholders"].Items)
                {
                    var prefix = "placeholders." + placeholder["id"].AsString() + ".";
                    Add(id, prefix + "default", placeholder["default"].AsString());
                    Add(id, prefix + "tooltip", placeholder["tooltip"].AsString());
                }
            }
        }

        return fields;
    }

    private static string[] MarkersOf(string text) =>
        Markers.Matches(text).Select(match => match.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static bool ContainsCjk(string text) =>
        text.Any(c => c is >= '　' and <= '〿'
            or >= '㐀' and <= '䶿'
            or >= '一' and <= '鿿'
            or >= '豈' and <= '﫿'
            or >= '＀' and <= '￯');
}
