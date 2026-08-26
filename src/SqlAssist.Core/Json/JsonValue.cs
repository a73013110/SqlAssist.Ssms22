using System;
using System.Collections.Generic;
using System.Globalization;

namespace SqlAssist.Core.Json;

/// <summary>JSON 值的種類。</summary>
public enum JsonKind
{
    Null,
    Boolean,
    Number,
    String,
    Array,
    Object
}

/// <summary>
/// 一個 JSON 值。
/// </summary>
/// <remarks>
/// 刻意自己寫而不引用 System.Text.Json：<see cref="SqlAssist.Core"/> 是
/// netstandard2.0 且零相依，而 VSIX 端多帶一份 System.Text.Json 進 SSMS 的行程
/// 就要面對組件版本綁定——SSMS 自己已經載了一份，版本不合時的症狀是擴充在載入期
/// 靜靜地掛掉。Snippet 檔的結構是固定且很小的一份，自己讀寫的成本遠低於那個風險。
///
/// 存取器一律是「拿不到就給預設值」而不是丟例外：這份檔案是使用者會自己編輯的，
/// 少一個欄位、型別打錯都要能救回來，只有整份檔案讀不成 JSON 才算失敗。
/// </remarks>
public sealed class JsonValue
{
    private static readonly IReadOnlyList<JsonValue> EmptyArray = Array.Empty<JsonValue>();

    private readonly Dictionary<string, JsonValue>? _members;
    private readonly IReadOnlyList<JsonValue>? _items;
    private readonly string? _text;
    private readonly double _number;
    private readonly bool _boolean;

    private JsonValue(JsonKind kind)
    {
        Kind = kind;
    }

    private JsonValue(bool value) : this(JsonKind.Boolean) => _boolean = value;

    private JsonValue(double value) : this(JsonKind.Number) => _number = value;

    private JsonValue(string value) : this(JsonKind.String) => _text = value;

    private JsonValue(IReadOnlyList<JsonValue> items) : this(JsonKind.Array) => _items = items;

    private JsonValue(Dictionary<string, JsonValue> members) : this(JsonKind.Object) => _members = members;

    public static readonly JsonValue Null = new(JsonKind.Null);

    public JsonKind Kind { get; }

    public static JsonValue FromBoolean(bool value) => new(value);

    public static JsonValue FromNumber(double value) => new(value);

    public static JsonValue FromString(string value) => new(value ?? string.Empty);

    public static JsonValue FromArray(IReadOnlyList<JsonValue> items) => new(items ?? EmptyArray);

    public static JsonValue FromObject(Dictionary<string, JsonValue> members) =>
        new(members ?? new Dictionary<string, JsonValue>(StringComparer.Ordinal));

    /// <summary>物件成員；不是物件或成員不存在時回傳 <see cref="Null"/>。</summary>
    public JsonValue this[string name]
    {
        get
        {
            if (_members is not null && name is not null && _members.TryGetValue(name, out var value))
            {
                return value;
            }

            return Null;
        }
    }

    /// <summary>陣列元素；不是陣列時回傳空集合。</summary>
    public IReadOnlyList<JsonValue> Items => _items ?? EmptyArray;

    /// <summary>物件成員名稱；順序不保證，只用於檢查有沒有認不得的欄位。</summary>
    public IEnumerable<string> Names => _members?.Keys ?? (IEnumerable<string>)Array.Empty<string>();

    public string AsString(string fallback = "")
    {
        return Kind == JsonKind.String ? _text! : fallback;
    }

    public bool AsBoolean(bool fallback = false)
    {
        return Kind == JsonKind.Boolean ? _boolean : fallback;
    }

    public int AsInt32(int fallback = 0)
    {
        return Kind == JsonKind.Number ? (int)_number : fallback;
    }

    public override string ToString()
    {
        return Kind switch
        {
            JsonKind.String => _text!,
            JsonKind.Number => _number.ToString(CultureInfo.InvariantCulture),
            JsonKind.Boolean => _boolean ? "true" : "false",
            JsonKind.Null => "null",
            _ => Kind.ToString()
        };
    }
}
