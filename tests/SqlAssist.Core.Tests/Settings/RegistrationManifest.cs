using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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

    /// <summary>整份文件；<c>enableWhen</c> 之類的結構性檢查直接看原始 JSON。</summary>
    public static JsonDocument Open()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SqlAssist.registration.json");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到註冊檔：{path}", path);
        }

        // 註冊檔帶註解（Unified Settings 的載入器接受 JSONC），解析時要略過。
        return JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
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
    private RegistrationSetting(string moniker, object @default, object alternate)
    {
        Moniker = moniker;
        Default = @default;
        Alternate = alternate;
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
                return new RegistrationSetting(moniker, value, value == maximum ? minimum : maximum);
            }

            case "string":
            {
                var value = declared.GetString()!;
                var alternate = declaration
                    .GetProperty("enum")
                    .EnumerateArray()
                    .Select(item => item.GetString()!)
                    .First(item => item != value);

                return new RegistrationSetting(moniker, value, alternate);
            }

            default:
                throw new NotSupportedException($"{moniker} 的型別未涵蓋：{type}");
        }
    }
}
