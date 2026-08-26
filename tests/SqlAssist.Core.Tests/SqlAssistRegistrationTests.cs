using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SqlAssist.Core;
using Xunit;

namespace SqlAssist.Core.Tests;

/// <summary>
/// <c>SqlAssist.registration.json</c> 與 <see cref="SqlAssistSettings"/> 的一致性。
/// </summary>
/// <remarks>
/// 兩邊各自宣告了一次預設值：註冊檔是設定 UI 上「恢復預設」會回到的值，
/// POCO 是讀不到 Unified Settings 時實際生效的值。它們一旦分歧，
/// 使用者會看到設定頁顯示一個值、擴充卻照另一個值運作，而且不會有任何錯誤。
/// 這份測試就是為了讓那種分歧變成建置失敗。
/// </remarks>
public sealed class SqlAssistRegistrationTests
{
    /// <remarks>必須宣告在 <c>ExpectedDefaults</c> 之前：靜態欄位依宣告順序初始化。</remarks>
    private static readonly SqlAssistSettings Defaults = new();

    /// <summary>moniker 對應到 <see cref="SqlAssistSettings"/> 上的預設值。</summary>
    private static readonly Dictionary<string, object> ExpectedDefaults = new()
    {
        ["sqlAssist.general.enabled"] = Defaults.Enabled,
        ["sqlAssist.general.uppercaseKeywordsOnType"] = Defaults.UppercaseKeywordsOnType,
        ["sqlAssist.general.expandWildcardOnTab"] = Defaults.ExpandWildcardOnTab,
        ["sqlAssist.suggestions.enabled"] = Defaults.SuggestionsEnabled,
        ["sqlAssist.suggestions.triggerAfterCharacters"] = Defaults.TriggerAfterCharacters,
        ["sqlAssist.suggestions.includeSnippets"] = Defaults.IncludeSnippets,
        ["sqlAssist.suggestions.includeDatabaseObjects"] = Defaults.IncludeDatabaseObjects,
        ["sqlAssist.suggestions.showCategoryFilters"] = Defaults.ShowCategoryFilters,
        ["sqlAssist.suggestions.qualifyObjectNames"] = Defaults.QualifyObjectNames,
        ["sqlAssist.suggestions.useSquareBrackets"] = Defaults.UseSquareBrackets,
        ["sqlAssist.structure.hoverEnabled"] = Defaults.HoverEnabled,
        ["sqlAssist.structure.previewMode"] = "delay",
        ["sqlAssist.structure.previewDelay"] = Defaults.PreviewDelayMilliseconds,
        ["sqlAssist.structure.previewPlacement"] = "stacked",
        ["sqlAssist.structure.previewFontSize"] = (int)Defaults.PreviewFontSize,
        ["sqlAssist.diagnostics.verboseLogging"] = Defaults.VerboseLogging
    };

    [Fact]
    public void 註冊檔宣告的設定與程式碼完全對應()
    {
        var properties = LoadProperties();

        Assert.Equal(
            ExpectedDefaults.Keys.Count,
            properties.Count);

        foreach (var moniker in ExpectedDefaults.Keys)
        {
            Assert.True(properties.ContainsKey(moniker), $"註冊檔缺少 {moniker}");
        }
    }

    [Fact]
    public void 註冊檔的預設值與程式碼的預設值一致()
    {
        var properties = LoadProperties();

        foreach (var (moniker, expected) in ExpectedDefaults)
        {
            var declared = properties[moniker].GetProperty("default");

            switch (expected)
            {
                case bool value:
                    Assert.Equal(value, declared.GetBoolean());
                    break;
                case int value:
                    Assert.Equal(value, declared.GetInt32());
                    break;
                case string value:
                    Assert.Equal(value, declared.GetString());
                    break;
                default:
                    Assert.Fail($"{moniker} 的預期型別未涵蓋：{expected.GetType()}");
                    break;
            }
        }
    }

    /// <remarks>
    /// 列舉值是字串，設定存放區裡存的就是這幾個字面值；
    /// 改名等於讓所有既有使用者的設定回退到預設值。
    /// </remarks>
    [Theory]
    [InlineData("sqlAssist.structure.previewMode", "delay", "rightArrow", "off")]
    [InlineData("sqlAssist.structure.previewPlacement", "stacked", "beside")]
    public void 列舉的字面值不變(string moniker, params string[] expected)
    {
        var declared = LoadProperties()[moniker].GetProperty("enum");
        var actual = new List<string>();

        foreach (var item in declared.EnumerateArray())
        {
            actual.Add(item.GetString()!);
        }

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// <c>enableWhen</c> 這類條件式只能參照同一個分類裡的設定。
    /// </summary>
    /// <remarks>
    /// 跨分類參照不會有任何錯誤訊息：殼層安靜地把整個設定丟掉，該分類的設定
    /// 全被丟掉之後就成了空分類，而空分類預設不顯示，於是整頁在設定視窗裡
    /// 人間蒸發。第一次實作時就是這樣讓「建議清單」與「物件結構」兩頁消失的，
    /// 而且從程式碼、schema 驗證到建置都看不出任何異狀。
    /// </remarks>
    [Fact]
    public void 條件式只參照同分類的設定()
    {
        var violations = new List<string>();

        foreach (var (owner, expression) in LoadConditions())
        {
            var category = owner[..owner.LastIndexOf('.')];

            foreach (var reference in ReferencedMonikers(expression))
            {
                var referencedCategory = reference.Contains('.')
                    ? reference[..reference.LastIndexOf('.')]
                    : reference;

                if (referencedCategory != category)
                {
                    violations.Add($"{owner} 參照了 {reference}（分類 {referencedCategory}）");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>所有條件式，配上宣告它的元素 moniker。</summary>
    private static IEnumerable<(string Owner, string Expression)> LoadConditions()
    {
        using var document = LoadDocument();
        var root = document.RootElement;
        var conditions = new List<(string, string)>();

        foreach (var property in root.GetProperty("properties").EnumerateObject())
        {
            if (property.Value.TryGetProperty("enableWhen", out var enableWhen))
            {
                conditions.Add((property.Name, enableWhen.GetString()!));
            }
        }

        // 分類上的 messages 與 commands 也帶條件式，適用同一條規則。
        foreach (var category in root.GetProperty("categories").EnumerateObject())
        {
            foreach (var name in new[] { "messages", "commands" })
            {
                if (!category.Value.TryGetProperty(name, out var items))
                {
                    continue;
                }

                foreach (var item in items.EnumerateArray())
                {
                    var element = item.TryGetProperty("vsct", out var vsct) ? vsct : item;

                    foreach (var key in new[] { "visibleWhen", "enableOnlyWhen" })
                    {
                        if (element.TryGetProperty(key, out var expression) &&
                            expression.ValueKind == JsonValueKind.String)
                        {
                            // 分類自己就是「同分類」的基準，補一段虛擬葉節點讓判斷一致。
                            conditions.Add(($"{category.Name}.{key}", expression.GetString()!));
                        }
                    }
                }
            }
        }

        return conditions;
    }

    private static IEnumerable<string> ReferencedMonikers(string expression)
    {
        const string Prefix = "${config:";
        var index = 0;

        while ((index = expression.IndexOf(Prefix, index, StringComparison.Ordinal)) >= 0)
        {
            var start = index + Prefix.Length;
            var end = expression.IndexOf('}', start);

            if (end < 0)
            {
                yield break;
            }

            yield return expression[start..end];
            index = end;
        }
    }

    /// <remarks>
    /// 註冊檔帶註解（Unified Settings 的載入器接受 JSONC），
    /// 所以解析時要允許並略過註解。
    /// </remarks>
    private static JsonDocument LoadDocument()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SqlAssist.registration.json");
        Assert.True(File.Exists(path), $"找不到註冊檔：{path}");

        return JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
    }

    private static Dictionary<string, JsonElement> LoadProperties()
    {
        using var document = LoadDocument();
        var properties = new Dictionary<string, JsonElement>();

        foreach (var property in document.RootElement.GetProperty("properties").EnumerateObject())
        {
            properties[property.Name] = property.Value.Clone();
        }

        return properties;
    }
}
