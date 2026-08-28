using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace SqlAssist.Core.Tests.Settings;

/// <summary>
/// <c>SqlAssist.registration.json</c> 本身的結構性規則。
/// </summary>
/// <remarks>
/// 註冊檔與程式碼之間的對應由 <see cref="SqlAssistSettingsReaderTests"/> 負責；
/// 這裡只管註冊檔自己要成立的事——那些寫錯了不會有任何錯誤訊息、
/// 只會讓設定頁默默少一塊的規則。
/// </remarks>
public sealed class SqlAssistRegistrationTests
{
    /// <summary>
    /// 列舉值是字串，設定存放區裡存的就是這幾個字面值；
    /// 改名等於讓所有既有使用者的設定回退到預設值。
    /// </summary>
    [Theory]
    [InlineData("sqlAssist.general.wildcardLayout", "oneLineWhenShort", "onePerLine", "fillWidth")]
    [InlineData("sqlAssist.structure.previewMode", "delay", "rightArrow", "off")]
    [InlineData("sqlAssist.structure.previewPlacement", "stacked", "beside")]
    public void 列舉的字面值不變(string moniker, params string[] expected)
    {
        using var document = RegistrationManifest.Open();

        var declared = document.RootElement
            .GetProperty("properties")
            .GetProperty(moniker)
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();

        Assert.Equal(expected.OrderBy(value => value, StringComparer.Ordinal), declared.OrderBy(value => value, StringComparer.Ordinal));
    }

    /// <summary>列舉的每一個值都要有對應的顯示文字，否則設定頁會列出空白項目。</summary>
    [Fact]
    public void 每個列舉值都有顯示文字()
    {
        using var document = RegistrationManifest.Open();

        foreach (var property in document.RootElement.GetProperty("properties").EnumerateObject())
        {
            if (!property.Value.TryGetProperty("enum", out var values))
            {
                continue;
            }

            Assert.True(
                property.Value.TryGetProperty("enumItemLabels", out var labels),
                $"{property.Name} 宣告了 enum 卻沒有 enumItemLabels");

            Assert.Equal(values.GetArrayLength(), labels.GetArrayLength());
        }
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

    /// <summary>條件式參照的 moniker 必須真的存在，打錯字同樣會讓整頁消失。</summary>
    [Fact]
    public void 條件式參照的設定都存在()
    {
        var known = new HashSet<string>(RegistrationManifest.Monikers, StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var (owner, expression) in LoadConditions())
        {
            foreach (var reference in ReferencedMonikers(expression))
            {
                if (!known.Contains(reference))
                {
                    violations.Add($"{owner} 參照了不存在的 {reference}");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>所有條件式，配上宣告它的元素 moniker。</summary>
    private static IEnumerable<(string Owner, string Expression)> LoadConditions()
    {
        using var document = RegistrationManifest.Open();
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
}
