using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using SqlAssist.Core.Json;

namespace SqlAssist.Core.Scripting;

/// <summary>
/// 把 <see cref="SqlScriptOptions"/> 存成使用者設定，以及讀回來。
/// </summary>
/// <remarks>
/// 存的是「哪一組風格，加上與那組風格<b>不同</b>的幾項」，不是完整的屬性清單。
/// 兩個理由：新增一個選項時，已經存過設定的人自動拿到它在該風格底下的預設值，
/// 不必寫遷移；而設定檔打開來看得出使用者到底改了什麼，全量輸出則是一整頁
/// 分不出哪幾項是他自己動的。
///
/// 屬性由反射列舉而不是手抄一份對應表。手抄的那一份會在新增選項時忘記跟上，
/// 而症狀是新選項存不進去也讀不回來，卻沒有任何錯誤——
/// <c>SqlScriptOptionsSerializerTests</c> 以同一份反射清單逐項往返，
/// 因此漏掉的型別支援會是測試失敗，不是安靜的資料遺失。
/// </remarks>
public static class SqlScriptOptionsSerializer
{
    private const string StyleMember = "style";
    private const string OverridesMember = "overrides";

    /// <summary>可存放的屬性，依名稱排序讓輸出穩定。</summary>
    internal static readonly IReadOnlyList<PropertyInfo> Properties =
        typeof(SqlScriptOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite &&
                               !string.Equals(property.Name, nameof(SqlScriptOptions.Style), StringComparison.Ordinal))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();

    public static string Write(SqlScriptOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var baseline = SqlScriptOptions.ForStyle(options.Style);

        return JsonWriter.Write(writer => writer.Object(root =>
        {
            root.Member(StyleMember, options.Style.ToString());
            root.Member(OverridesMember, overrides => overrides.Object(body =>
            {
                foreach (var property in Properties)
                {
                    var value = property.GetValue(options);

                    if (Equals(value, property.GetValue(baseline)))
                    {
                        continue;
                    }

                    WriteMember(body, CamelCase(property.Name), value);
                }
            }));
        }));
    }

    /// <summary>
    /// 讀回選項；任何一項讀不出來就沿用該風格的預設值。
    /// </summary>
    /// <remarks>
    /// 設定檔壞掉不該讓產生指令碼整個失效——使用者要的是那份指令碼，不是一則
    /// 剖析錯誤。壞掉的是整份 JSON 時退回 Fidelity 風格，壞掉的是其中一項時
    /// 只有那一項退回預設值。
    /// </remarks>
    public static SqlScriptOptions Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SqlScriptOptions.Fidelity;
        }

        JsonValue root;

        try
        {
            root = JsonReader.Parse(text!);
        }
        catch (JsonParseException)
        {
            return SqlScriptOptions.Fidelity;
        }

        var style = ParseEnum(root[StyleMember].AsString(), SqlScriptStyle.Fidelity);
        var result = Copy(SqlScriptOptions.ForStyle(style), style);
        var overrides = root[OverridesMember];

        foreach (var property in Properties)
        {
            var member = overrides[CamelCase(property.Name)];

            if (member.Kind == JsonKind.Null)
            {
                continue;
            }

            if (TryReadValue(property.PropertyType, member, out var value))
            {
                property.SetValue(result, value);
            }
        }

        return result;
    }

    /// <remarks>
    /// 逐項複製而不是 <c>with</c>：反射要寫進去的是一個新的實例，
    /// 直接對 <see cref="SqlScriptOptions.Fidelity"/> 那幾個共用的靜態值
    /// <c>SetValue</c> 會改掉所有人的預設值。
    /// </remarks>
    private static SqlScriptOptions Copy(SqlScriptOptions source, SqlScriptStyle style)
    {
        var result = new SqlScriptOptions { Style = style };

        foreach (var property in Properties)
        {
            property.SetValue(result, property.GetValue(source));
        }

        return result;
    }

    private static void WriteMember(JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case bool boolean:
                writer.Member(name, boolean);
                break;
            case string text:
                writer.Member(name, text);
                break;
            case int number:
                writer.Member(name, number);
                break;
            default:
                // 列舉一律寫字面值。寫序數的話重新排列成員就會把舊設定讀成另一個值。
                writer.Member(name, value?.ToString() ?? string.Empty);
                break;
        }
    }

    private static bool TryReadValue(Type type, JsonValue member, out object? value)
    {
        if (type == typeof(bool))
        {
            value = member.AsBoolean();
            return member.Kind == JsonKind.Boolean;
        }

        if (type == typeof(string))
        {
            value = member.AsString();
            return member.Kind == JsonKind.String;
        }

        if (type == typeof(int))
        {
            value = member.AsInt32();
            return member.Kind == JsonKind.Number;
        }

        if (type.IsEnum && member.Kind == JsonKind.String)
        {
            return TryParseEnum(type, member.AsString(), out value);
        }

        value = null;
        return false;
    }

    private static bool TryParseEnum(Type type, string text, out object? value)
    {
        foreach (var name in Enum.GetNames(type))
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                value = Enum.Parse(type, name);
                return true;
            }
        }

        value = null;
        return false;
    }

    private static TEnum ParseEnum<TEnum>(string text, TEnum fallback)
        where TEnum : struct
    {
        return TryParseEnum(typeof(TEnum), text, out var value) && value is TEnum parsed
            ? parsed
            : fallback;
    }

    private static string CamelCase(string name)
    {
        return name.Length == 0
            ? name
            : char.ToLower(name[0], CultureInfo.InvariantCulture) + name.Substring(1);
    }
}
