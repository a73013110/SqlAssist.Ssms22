using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlAssist.Core.Localization;

namespace SqlAssist.Core.Tests.Settings;

/// <summary>
/// 測試用的 <c>SqlAssist.registration.json</c> 讀取器。
/// </summary>
/// <remarks>
/// 註冊檔是這個擴充「有哪些設定」的唯一權威來源：SSMS 照它畫設定頁、
/// 照它決定預設值與範圍。所以測試一律以它為基準反推，不再手抄一份
/// moniker 清單——手抄的那一份漏掉新設定時，測試只會安靜地少驗一項。
/// </remarks>
internal static class RegistrationManifest
{
    /// <summary>註冊檔宣告的每一個設定，依 moniker 排序。</summary>
    public static readonly IReadOnlyList<RegistrationSetting> Settings = Load();

    /// <summary>全部 moniker，依序數排序。</summary>
    public static readonly IReadOnlyList<string> Monikers =
        Settings.Select(setting => setting.Moniker).ToArray();

    /// <summary>每一個設定在註冊檔宣告的預設值，型別已轉成 <c>ISettingValueSource</c> 會回傳的樣子。</summary>
    public static IReadOnlyDictionary<string, object> DefaultValues =>
        Settings.ToDictionary(setting => setting.Moniker, setting => setting.Default);

    /// <summary>
    /// 顯示文字引用的套件；必須等於 <c>SqlAssistPackage.PackageGuidString</c>，
    /// 否則 SSMS 找不到資源，設定頁把 "@鍵;{guid}" 原樣畫出來。
    /// </summary>
    public const string PackageGuid = "b386e18d-f34b-4db4-a40d-b9092a31d89f";

    private static readonly Regex ResourceReference =
        new(@"^@(?<key>[A-Za-z0-9]+);\{(?<guid>[^}]+)\}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 各語言的設定頁文字（<c>Settings/SettingsPageText.&lt;語言&gt;.resjson</c>），以語言名稱為鍵。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Texts = LoadTexts();

    /// <summary>整份文件；<c>enableWhen</c> 之類的結構性檢查直接看原始 JSON。</summary>
    public static JsonDocument Open()
    {
        // 註冊檔帶註解（Unified Settings 的載入器接受 JSONC），解析時要略過。
        return JsonDocument.Parse(
            ReadOutputFile("SqlAssist.registration.json"),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
    }

    /// <summary>
    /// 註冊檔裡每一個 SSMS 會顯示的文字欄位與原始值（應為資源引用），路徑形如
    /// <c>properties.sqlAssist.general.enabled.title</c>。
    /// </summary>
    /// <remarks>
    /// 涵蓋 title、description、enumItemLabels、messages 與 commands 的 text；
    /// additionalKeywords 只供搜尋比對、不顯示，維持原文。
    /// </remarks>
    public static IReadOnlyList<(string Path, string Value)> DisplayTexts()
    {
        using var document = Open();
        var texts = new List<(string, string)>();

        foreach (var section in new[] { "properties", "categories" })
        {
            foreach (var owner in document.RootElement.GetProperty(section).EnumerateObject())
            {
                var path = $"{section}.{owner.Name}";

                foreach (var field in new[] { "title", "description" })
                {
                    if (owner.Value.TryGetProperty(field, out var value))
                    {
                        texts.Add(($"{path}.{field}", value.GetString()!));
                    }
                }

                if (owner.Value.TryGetProperty("enumItemLabels", out var labels))
                {
                    texts.AddRange(labels.EnumerateArray().Select((label, index) => ($"{path}.enumItemLabels[{index}]", label.GetString()!)));
                }

                foreach (var field in new[] { "messages", "commands" })
                {
                    if (!owner.Value.TryGetProperty(field, out var items))
                    {
                        continue;
                    }

                    foreach (var (item, index) in items.EnumerateArray().Select((item, index) => (item, index)))
                    {
                        var element = item.TryGetProperty("vsct", out var vsct) ? vsct : item;
                        texts.Add(($"{path}.{field}[{index}].text", element.GetProperty("text").GetString()!));
                    }
                }
            }
        }

        return texts;
    }

    /// <summary>資源引用的鍵；不是 "@鍵;{guid}" 格式時回傳 null。</summary>
    public static (string Key, string Guid)? ParseReference(string value)
    {
        var match = ResourceReference.Match(value);
        return match.Success ? (match.Groups["key"].Value, match.Groups["guid"].Value) : null;
    }

    /// <summary>把註冊檔裡的顯示文字解析成 SSMS 在該介面語言下會畫出的文字。</summary>
    public static string Resolve(string value, string language)
    {
        var reference = ParseReference(value) ??
            throw new InvalidOperationException($"顯示文字不是資源引用：{value}");

        return Texts[language].TryGetValue(reference.Key, out var text)
            ? text
            : throw new KeyNotFoundException($"SettingsPageText.{language}.resjson 沒有鍵 {reference.Key}");
    }

    /// <summary>設定或分類某個欄位解析後的文字，例如 <c>Text("sqlAssist.general.enabled", "title", "en")</c>。</summary>
    public static string Text(string moniker, string field, string language)
    {
        using var document = Open();
        var root = document.RootElement;
        var owner = root.GetProperty("properties").TryGetProperty(moniker, out var setting)
            ? setting
            : root.GetProperty("categories").GetProperty(moniker);

        return Resolve(owner.GetProperty(field).GetString()!, language);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadTexts()
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        return SqlLanguage.All.ToDictionary(
            language => language.Name,
            language =>
            {
                using var document = JsonDocument.Parse(ReadOutputFile($"SettingsPageText.{language.Name}.resjson"), options);
                return (IReadOnlyDictionary<string, string>)document.RootElement
                    .EnumerateObject()
                    .ToDictionary(pair => pair.Name, pair => pair.Value.GetString()!, StringComparer.Ordinal);
            },
            StringComparer.Ordinal);
    }

    private static string ReadOutputFile(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到 {name}：{path}", path);
        }

        return File.ReadAllText(path);
    }

    private static RegistrationSetting[] Load()
    {
        using var document = Open();

        return document.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => RegistrationSetting.From(property.Name, property.Value))
            .OrderBy(setting => setting.Moniker, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>註冊檔裡的一個設定。</summary>
internal sealed class RegistrationSetting
{
    private RegistrationSetting(
        string moniker,
        object @default,
        object alternate,
        RegistrationBounds? bounds = null)
    {
        Moniker = moniker;
        Default = @default;
        Alternate = alternate;
        Bounds = bounds;
    }

    public string Moniker { get; }

    /// <summary>註冊檔宣告的預設值。</summary>
    public object Default { get; }

    /// <summary>
    /// 一個保證與 <see cref="Default"/> 不同、且落在合法範圍內的值。
    /// </summary>
    /// <remarks>
    /// 用來檢查「這個 moniker 真的被讀進某個屬性」：只改這一項，快照就必須跟著變。
    /// 數值取邊界而不是隨意加一，這樣一定通得過讀取端的收斂。
    /// </remarks>
    public object Alternate { get; }

    /// <summary>數值設定在註冊檔宣告的上下限；其他型別為 null。</summary>
    public RegistrationBounds? Bounds { get; }

    public static RegistrationSetting From(string moniker, JsonElement declaration)
    {
        var type = declaration.GetProperty("type").GetString();
        var declared = declaration.GetProperty("default");

        switch (type)
        {
            case "boolean":
            {
                var value = declared.GetBoolean();
                return new RegistrationSetting(moniker, value, !value);
            }

            case "integer":
            {
                var value = declared.GetInt32();
                var minimum = declaration.GetProperty("minimum").GetInt32();
                var maximum = declaration.GetProperty("maximum").GetInt32();
                return new RegistrationSetting(
                    moniker,
                    value,
                    value == maximum ? minimum : maximum,
                    new RegistrationBounds(minimum, maximum));
            }

            case "string":
            {
                var value = declared.GetString()!;
                var alternate = declaration.TryGetProperty("enum", out var choices) ? choices
                    .EnumerateArray()
                    .Select(item => item.GetString()!)
                    .First(item => item != value) : value == "#4F86C6" ? "#8050B0" : "#4F86C6";

                return new RegistrationSetting(moniker, value, alternate);
            }

            default:
                throw new NotSupportedException($"{moniker} 的型別未涵蓋：{type}");
        }
    }
}

/// <summary>數值設定在註冊檔宣告的範圍。</summary>
internal readonly struct RegistrationBounds
{
    public RegistrationBounds(int minimum, int maximum)
    {
        Minimum = minimum;
        Maximum = maximum;
    }

    public int Minimum { get; }

    public int Maximum { get; }
}
