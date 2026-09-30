using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace SqlAssist.CompletionAudit.Runner;

/// <summary>
/// 稽核的 JSON：框架內建的 <see cref="JavaScriptSerializer"/>，讀成字典、寫匿名物件。
/// </summary>
/// <remarks>
/// 不用 System.Text.Json：它要 System.Memory 那一串 BCL 相依，而這個輸出目錄刻意不帶它們（見專案檔），
/// 全靠 SSMS 那一份；少一個套件就少一個版本要對齊。
/// </remarks>
internal static class AuditJson
{
    private static readonly JavaScriptSerializer Serializer = new() { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

    public static string Serialize(object value) => Serializer.Serialize(value);

    /// <summary>讀成 <c>Dictionary&lt;string, object&gt;</c>、<c>ArrayList</c>（<c>object[]</c>）與純量。</summary>
    public static Dictionary<string, object> Parse(string json) =>
        Serializer.DeserializeObject(json) as Dictionary<string, object>
            ?? throw new FormatException("JSON 的最上層不是物件。");

    public static string? String(this Dictionary<string, object> node, string name) =>
        node.TryGetValue(name, out var value) ? value as string : null;

    public static string RequiredString(this Dictionary<string, object> node, string name) =>
        node.String(name) ?? throw new FormatException($"JSON 缺少 {name}。");

    public static int Int(this Dictionary<string, object> node, string name, int fallback = 0) =>
        node.TryGetValue(name, out var value) && value is not null ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : fallback;

    public static bool Bool(this Dictionary<string, object> node, string name) =>
        node.TryGetValue(name, out var value) && value is bool flag && flag;

    public static bool? NullableBool(this Dictionary<string, object> node, string name) =>
        node.TryGetValue(name, out var value) ? value as bool? : null;

    public static Dictionary<string, object> Object(this Dictionary<string, object> node, string name) =>
        node.TryGetValue(name, out var value) && value is Dictionary<string, object> child ? child : new Dictionary<string, object>();

    public static IEnumerable<object> Array(this Dictionary<string, object> node, string name) =>
        node.TryGetValue(name, out var value) && value is IEnumerable items and not string
            ? Items(items)
            : System.Array.Empty<object>();

    private static IEnumerable<object> Items(IEnumerable items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
    }
}
