using System;
using System.Collections.Generic;
using SqlAssist.Core.Json;

namespace SqlAssist.Core.Snippets;

/// <summary>
/// Snippet 清單與 JSON 之間的轉換。
/// </summary>
/// <remarks>
/// 格式是 SqlAssist 自己的，不是 SSMS 的 <c>.snippet</c> XML。檔案長這樣：
///
/// <code>
/// {
///   "version": 1,
///   "snippets": [
///     {
///       "shortcut": "ssf",
///       "title": "SELECT * FROM",
///       "description": "SELECT * FROM fragment",
///       "triggerFollowUp": true,
///       "code": "SELECT * FROM $table$$end$",
///       "placeholders": [
///         { "id": "table", "default": "", "tooltip": "資料表名稱" }
///       ]
///     }
///   ]
/// }
/// </code>
///
/// 讀取一律寬容：認不得的欄位略過，缺的欄位補預設值，壞掉的單一項目跳過而不是
/// 讓整份檔案失敗。使用者會自己用記事本改這個檔，一個打錯的逗號不該讓所有
/// Snippet 一起消失。只有整份內容不是 JSON 時才丟例外。
/// </remarks>
public static class SqlSnippetSerializer
{
    public static string Serialize(SqlSnippetLibrary library)
    {
        if (library is null)
        {
            throw new ArgumentNullException(nameof(library));
        }

        return JsonWriter.Write(writer => writer.Object(root =>
        {
            root.Member("version", SqlSnippetLibrary.CurrentVersion);
            root.Member("snippets", snippets => snippets.Array(
                library.Snippets,
                (item, snippet) => item.Object(fields =>
                {
                    fields.Member("shortcut", snippet.Shortcut);
                    fields.Member("title", snippet.Title);
                    fields.Member("description", snippet.Description);
                    fields.Member("triggerFollowUp", snippet.TriggerFollowUp);
                    fields.Member("code", snippet.Code);

                    if (snippet.Placeholders.Count == 0)
                    {
                        return;
                    }

                    fields.Member("placeholders", placeholders => placeholders.Array(
                        snippet.Placeholders,
                        (entry, placeholder) => entry.Object(values =>
                        {
                            values.Member("id", placeholder.Id);
                            values.Member("default", placeholder.DefaultValue);
                            values.Member("tooltip", placeholder.ToolTip);
                        })));
                })));
        }));
    }

    /// <summary>剖析一份 Snippet 檔。</summary>
    /// <exception cref="JsonParseException">內容不是合法的 JSON。</exception>
    public static SqlSnippetLibrary Deserialize(string text)
    {
        var root = JsonReader.Parse(text);
        var snippets = new List<SqlSnippet>();

        foreach (var entry in root["snippets"].Items)
        {
            var shortcut = entry["shortcut"].AsString();
            var code = entry["code"].AsString();

            // 沒有捷徑或沒有內容的項目展不開也列不出來，留著只會在管理介面裡
            // 變成一列看不懂的空白。
            if (string.IsNullOrWhiteSpace(shortcut) || code.Length == 0)
            {
                continue;
            }

            snippets.Add(new SqlSnippet(
                shortcut,
                code,
                entry["title"].AsString(),
                entry["description"].AsString(),
                entry["triggerFollowUp"].AsBoolean(),
                ReadPlaceholders(entry["placeholders"])));
        }

        return new SqlSnippetLibrary(snippets);
    }

    private static IReadOnlyList<SqlSnippetPlaceholder> ReadPlaceholders(JsonValue value)
    {
        if (value.Items.Count == 0)
        {
            return Array.Empty<SqlSnippetPlaceholder>();
        }

        var placeholders = new List<SqlSnippetPlaceholder>(value.Items.Count);

        foreach (var entry in value.Items)
        {
            var id = entry["id"].AsString();

            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            placeholders.Add(new SqlSnippetPlaceholder(
                id,
                entry["default"].AsString(),
                entry["tooltip"].AsString()));
        }

        return placeholders;
    }
}
