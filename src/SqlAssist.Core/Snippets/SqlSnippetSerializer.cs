using System;
using System.Collections.Generic;
using SqlAssist.Core.Json;
using SqlAssist.Core.Keywords;

namespace SqlAssist.Core.Snippets;

/// <summary>SqlAssist Snippet JSON 的寬容讀取與穩定輸出。</summary>
public static class SqlSnippetSerializer
{
    /// <summary>相容舊呼叫端：把整份清單寫成 v2 的完整紀錄。</summary>
    public static string Serialize(SqlSnippetLibrary library)
    {
        if (library is null)
        {
            throw new ArgumentNullException(nameof(library));
        }

        var records = new List<SqlSnippetOverride>(library.Count);

        foreach (var snippet in library.Snippets)
        {
            records.Add(new SqlSnippetOverride(snippet.Id, disabled: false, snippet));
        }

        return Serialize(new SqlSnippetDocument(SqlSnippetLibrary.CurrentVersion, records));
    }

    public static string Serialize(SqlSnippetDocument document)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return JsonWriter.Write(writer => writer.Object(root =>
        {
            root.Member("version", document.Version);
            root.Member("snippets", snippets => snippets.Array(
                document.Snippets,
                (item, record) => WriteRecord(item, record)));
        }));
    }

    /// <summary>相容舊呼叫端：只回傳檔案裡啟用且有完整內容的項目。</summary>
    public static SqlSnippetLibrary Deserialize(string text)
    {
        var document = DeserializeDocument(text);
        var snippets = new List<SqlSnippet>(document.Snippets.Count);

        foreach (var record in document.Snippets)
        {
            if (!record.Disabled && record.Snippet is { } snippet)
            {
                snippets.Add(snippet);
            }
        }

        return new SqlSnippetLibrary(snippets);
    }

    /// <summary>剖析一份 Snippet 檔；版本缺席時視為 v1。</summary>
    /// <exception cref="JsonParseException">內容不是合法的 JSON。</exception>
    public static SqlSnippetDocument DeserializeDocument(string text)
    {
        var root = JsonReader.Parse(text);
        var version = root["version"].AsInt32(1);

        if (version < 1)
        {
            version = 1;
        }

        var records = new List<SqlSnippetOverride>();

        foreach (var entry in root["snippets"].Items)
        {
            var id = entry["id"].AsString();
            var disabled = entry["disabled"].AsBoolean();
            var shortcut = entry["shortcut"].AsString();
            var code = entry["code"].AsString();

            if (disabled && !string.IsNullOrWhiteSpace(id))
            {
                records.Add(new SqlSnippetOverride(id, disabled: true));
                continue;
            }

            // 壞掉的單一項目不拖垮整份檔案；管理介面仍會顯示整份 JSON 的語法錯誤。
            if (string.IsNullOrWhiteSpace(shortcut) || code.Length == 0)
            {
                continue;
            }

            var placeholders = SqlSnippetPlaceholders.Reconcile(
                code,
                ReadPlaceholders(entry["placeholders"]));
            var expansionMode = ParseExpansionMode(entry["expansionMode"].AsString());

            if (expansionMode == SqlSnippetExpansionMode.TabStops && placeholders.Count == 0)
            {
                // 沒有可導航欄位時啟動原生 session 沒有價值，且會多出第三條按鍵路徑。
                expansionMode = SqlSnippetExpansionMode.Caret;
            }

            var snippet = new SqlSnippet(
                shortcut,
                code,
                entry["title"].AsString(),
                entry["description"].AsString(),
                entry["triggerFollowUp"].AsBoolean(),
                placeholders,
                id,
                ParseCategory(entry["category"].AsString()),
                entry["isDestructive"].AsBoolean(),
                expansionMode,
                ReadPositions(entry["positions"]));

            records.Add(new SqlSnippetOverride(id, disabled: false, snippet));
        }

        return new SqlSnippetDocument(version, records);
    }

    private static void WriteRecord(JsonWriter item, SqlSnippetOverride record)
    {
        item.Object(fields =>
        {
            fields.Member("id", record.Id);

            if (record.Disabled)
            {
                fields.Member("disabled", true);
                return;
            }

            if (record.Snippet is not { } snippet)
            {
                return;
            }

            fields.Member("category", CategoryName(snippet.Category));
            fields.Member("shortcut", snippet.Shortcut);
            fields.Member("title", snippet.Title);
            fields.Member("description", snippet.Description);

            if (snippet.IsDestructive)
            {
                fields.Member("isDestructive", true);
            }

            if (snippet.TriggerFollowUp)
            {
                fields.Member("triggerFollowUp", true);
            }

            fields.Member("expansionMode", ExpansionModeName(snippet.ExpansionMode));
            WritePositions(fields, snippet.Positions);
            fields.Member("code", snippet.Code);

            if (snippet.Placeholders.Count > 0)
            {
                fields.Member("placeholders", placeholders => placeholders.Array(
                    snippet.Placeholders,
                    (entry, placeholder) => entry.Object(values =>
                    {
                        values.Member("id", placeholder.Id);
                        values.Member("default", placeholder.DefaultValue);
                        values.Member("tooltip", placeholder.ToolTip);
                    })));
            }
        });
    }

    private static IReadOnlyList<SqlSnippetPlaceholder> ReadPlaceholders(JsonValue value)
    {
        var placeholders = new List<SqlSnippetPlaceholder>(value.Items.Count);

        foreach (var entry in value.Items)
        {
            var id = entry["id"].AsString();

            if (!string.IsNullOrWhiteSpace(id))
            {
                placeholders.Add(new SqlSnippetPlaceholder(
                    id,
                    entry["default"].AsString(),
                    entry["tooltip"].AsString()));
            }
        }

        return placeholders;
    }

    private static SqlSnippetCategory ParseCategory(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "select" => SqlSnippetCategory.Select,
            "dml" => SqlSnippetCategory.Dml,
            "ddl" => SqlSnippetCategory.Ddl,
            "controlflow" => SqlSnippetCategory.ControlFlow,
            "clause" => SqlSnippetCategory.Clause,
            _ => SqlSnippetCategory.Other
        };
    }

    private static string CategoryName(SqlSnippetCategory value)
    {
        return value switch
        {
            SqlSnippetCategory.Select => "select",
            SqlSnippetCategory.Dml => "dml",
            SqlSnippetCategory.Ddl => "ddl",
            SqlSnippetCategory.ControlFlow => "controlFlow",
            SqlSnippetCategory.Clause => "clause",
            _ => "other"
        };
    }

    private static SqlSnippetExpansionMode ParseExpansionMode(string value)
    {
        return string.Equals(value, "tabStops", StringComparison.OrdinalIgnoreCase)
            ? SqlSnippetExpansionMode.TabStops
            : SqlSnippetExpansionMode.Caret;
    }

    private static string ExpansionModeName(SqlSnippetExpansionMode value) =>
        value == SqlSnippetExpansionMode.TabStops ? "tabStops" : "caret";

    private static SqlKeywordPosition ReadPositions(JsonValue value)
    {
        if (value.Kind == JsonKind.String)
        {
            var single = ParsePosition(value.AsString());
            return single == SqlKeywordPosition.None ? SqlKeywordPosition.Any : single;
        }

        if (value.Items.Count == 0)
        {
            return SqlKeywordPosition.Any;
        }

        var result = SqlKeywordPosition.None;

        foreach (var item in value.Items)
        {
            result |= ParsePosition(item.AsString());
        }

        return result == SqlKeywordPosition.None ? SqlKeywordPosition.Any : result;
    }

    private static SqlKeywordPosition ParsePosition(string value)
    {
        return Enum.TryParse(value, ignoreCase: true, out SqlKeywordPosition position)
            ? position
            : SqlKeywordPosition.None;
    }

    private static void WritePositions(JsonWriter fields, SqlKeywordPosition positions)
    {
        if (positions == SqlKeywordPosition.Any)
        {
            return;
        }

        var values = new List<string>();

        foreach (SqlKeywordPosition candidate in Enum.GetValues(typeof(SqlKeywordPosition)))
        {
            if (candidate is SqlKeywordPosition.None or SqlKeywordPosition.Any ||
                (positions & candidate) == SqlKeywordPosition.None)
            {
                continue;
            }

            values.Add(candidate.ToString());
        }

        if (values.Count > 0)
        {
            fields.Member("positions", writer => writer.Array(values, (entry, value) => entry.Value(value)));
        }
    }
}
