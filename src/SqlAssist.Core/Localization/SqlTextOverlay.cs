using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using SqlAssist.Core.Json;

namespace SqlAssist.Core.Localization;

/// <summary>資料型文字（內嵌 JSON）的語言覆蓋檔：只帶要翻的欄位，疊在來源資料上。</summary>
/// <remarks>
/// <c>BuiltInDocs.json</c> 這種資料不進 .resjson 產生器：一筆有好幾個欄位、還有巢狀的對照表，
/// 攤成幾百個產生成員只會讓資料與程式各留一半。來源檔維持繁中，旁邊放同名的
/// <c>BuiltInDocs.en.json</c>，形狀是「編號 → 欄位 → 譯文」：
/// <code>{ "CONVERT": { "summary": "…", "example": "…" }, "tables.dateStyles": { "rows.0.1": "…" } }</code>
/// 欄位名稱由各載入器自己定（巢狀或陣列位置用點號串起來），這裡只負責查表；
/// 查不到就用來源那一句，所以來源語言本身不需要覆蓋檔。
///
/// 讀不到或格式壞掉一律降級成空的覆蓋（整份回到來源語言），<b>不</b>丟例外，理由與各資源
/// 載入器相同：那是建置期的錯，由覆蓋檔的測試守，而執行期這條路掛在按鍵與滑鼠停留上。
/// </remarks>
public sealed class SqlTextOverlay
{
    private static readonly IReadOnlyDictionary<string, string> NoFields = new Dictionary<string, string>();

    private readonly Dictionary<string, Dictionary<string, string>> _entries;

    private SqlTextOverlay(Dictionary<string, Dictionary<string, string>> entries, string? error)
    {
        _entries = entries;
        Error = error;
    }

    /// <summary>沒有任何譯文；來源語言用的就是它。</summary>
    public static SqlTextOverlay Empty { get; } = new(new(StringComparer.Ordinal), null);

    /// <summary>載入失敗的原因，只給測試與診斷；成功或不需要覆蓋檔時為 null。</summary>
    public string? Error { get; }

    /// <summary>有譯文的編號。</summary>
    public IReadOnlyCollection<string> Ids => _entries.Keys;

    /// <summary>某一筆翻了哪些欄位。</summary>
    public IReadOnlyDictionary<string, string> FieldsOf(string id) =>
        _entries.TryGetValue(id, out var fields) ? fields : NoFields;

    /// <summary>這一筆這個欄位的譯文；沒有就回傳來源那一句。</summary>
    public string Apply(string id, string field, string source) =>
        _entries.TryGetValue(id, out var fields) && fields.TryGetValue(field, out var text) ? text : source;

    /// <summary>來源資源名稱加上語言：<c>X.BuiltInDocs.json</c> → <c>X.BuiltInDocs.en.json</c>。</summary>
    public static string ResourceNameFor(string sourceResourceName, SqlLanguage language)
    {
        if (sourceResourceName is null)
        {
            throw new ArgumentNullException(nameof(sourceResourceName));
        }

        if (language is null)
        {
            throw new ArgumentNullException(nameof(language));
        }

        var extension = Path.GetExtension(sourceResourceName);
        return sourceResourceName.Substring(0, sourceResourceName.Length - extension.Length)
            + "." + language.Name + extension;
    }

    /// <summary>讀出某個來源資源在指定語言的覆蓋檔；來源語言直接回傳 <see cref="Empty"/>。</summary>
    [Localizable(false)]
    public static SqlTextOverlay Load(Assembly assembly, string sourceResourceName, SqlLanguage language)
    {
        if (assembly is null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        if (language is null)
        {
            throw new ArgumentNullException(nameof(language));
        }

        if (language == SqlLanguage.Source)
        {
            return Empty;
        }

        var name = ResourceNameFor(sourceResourceName, language);

        try
        {
            using var stream = assembly.GetManifestResourceStream(name);

            if (stream is null)
            {
                return Failed($"找不到語言覆蓋檔：{name}");
            }

            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception exception)
        {
            return Failed($"語言覆蓋檔 {name} 讀取失敗：{exception.Message}");
        }
    }

    /// <summary>剖析覆蓋檔內容；格式不對的那一筆或那一欄略過。</summary>
    public static SqlTextOverlay Parse(string json)
    {
        var root = JsonReader.Parse(json);
        var entries = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        foreach (var id in root.Names)
        {
            var node = root[id];
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var field in node.Names)
            {
                if (node[field].Kind == JsonKind.String)
                {
                    fields[field] = node[field].AsString();
                }
            }

            if (fields.Count > 0)
            {
                entries[id] = fields;
            }
        }

        return new SqlTextOverlay(entries, null);
    }

    private static SqlTextOverlay Failed(string error) => new(new(StringComparer.Ordinal), error);
}
