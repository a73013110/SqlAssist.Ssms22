using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SqlAssist.Core.Json;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Keywords;

/// <summary>
/// 資料格式驗證：拆檔後的形狀、<c>references</c> 兩種寫法、覆蓋檔引用與範例的詞法正確性。
/// </summary>
/// <remarks>
/// 這裡直接讀內嵌資源的原始 JSON，不透過 <see cref="SqlBuiltInDocCatalog"/>：合併之後只剩
/// 「查得到查不到」，看不出「這一筆寫在哪個檔案」、「這一筆是不是同一個名稱寫了兩次」，
/// 而這些規則正是要在合併之前守住。內容本身（範例是否寫得好）留給撰寫時的人工審閱，
/// 這裡只守機械式看得出來的錯：欄位缺漏、種類放錯檔、引用打錯字、字串沒收尾。
/// </remarks>
public sealed class SqlBuiltInDocsFormatTests
{
    private const string ResourceFolder = "SqlAssist.Core.Keywords.BuiltInDocs.";

    private sealed record DocFile(string FileName, string[] AllowedKinds, bool RequiresSignature);

    private static readonly DocFile[] DocFiles =
    {
        new("functions.json", new[] { "function" }, false),
        new("types-hints.json", new[] { "dataType", "tableHint", "queryHint", "datePart", "globalVariable" }, false),
        new("statements.json", new[] { "statement" }, true),
        new("system-procedures.json", new[] { "systemProcedure" }, true)
    };

    private static JsonValue LoadResource(string fileName)
    {
        var assembly = typeof(SqlBuiltInDocCatalog).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceFolder + fileName);
        Assert.True(stream is not null, $"找不到資源：{fileName}");

        using var reader = new StreamReader(stream!);
        return JsonReader.Parse(reader.ReadToEnd());
    }

    private static JsonValue LoadOverlay(string fileName)
    {
        var overlayName = fileName.Substring(0, fileName.Length - ".json".Length) + ".en.json";
        return LoadResource(overlayName);
    }

    private static IReadOnlyList<string> TableIds(JsonValue tables) => tables.Names.ToList();

    /// <summary>種類要放對檔案：functions.json 只放 Function，其餘同理。</summary>
    [Fact]
    public void 各檔的條目種類必須屬於該檔()
    {
        foreach (var file in DocFiles)
        {
            var root = LoadResource(file.FileName);

            Assert.All(root["docs"].Items, item =>
            {
                var name = item["name"].AsString();
                var kind = item["kind"].AsString();
                Assert.Contains(kind, file.AllowedKinds);
            });
        }
    }

    /// <summary>同一筆的 example id 不重複；有範例時 title／sql 不為空。</summary>
    [Fact]
    public void 範例編號不重複且內容不為空()
    {
        foreach (var file in DocFiles)
        {
            var root = LoadResource(file.FileName);

            foreach (var item in root["docs"].Items)
            {
                var name = item["name"].AsString();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var example in item["examples"].Items)
                {
                    var id = example["id"].AsString();
                    Assert.True(id.Length > 0, $"{file.FileName}／{name}：example 缺 id");
                    Assert.True(seen.Add(id), $"{file.FileName}／{name}：example id 重複：{id}");
                    Assert.True(example["title"].AsString().Length > 0, $"{file.FileName}／{name}／{id}：title 為空");
                    Assert.True(example["sql"].AsString().Length > 0, $"{file.FileName}／{name}／{id}：sql 為空");
                }
            }
        }
    }

    /// <summary>
    /// 名稱（含 <c>aliases</c>）不分大小寫跨所有檔案都不得重複；種類不參與比對。
    /// </summary>
    /// <remarks>
    /// <see cref="SqlBuiltInDocCatalog"/> 合併時只用名稱當鍵（<c>Dictionary</c> 用
    /// <c>StringComparer.OrdinalIgnoreCase</c>，見 <c>ReadDocs</c> 的
    /// <c>entries[name] = entry</c>），不含種類——「種類要放對檔案」那條測試擋得住
    /// 同一個名字寫錯種類，擋不住兩個不同種類搶同一個名字：<c>YEAR</c> 若同時出現在
    /// functions.json 與 types-hints.json，後讀到的那份會安靜蓋掉前面的說明，執行期
    /// 兩者看起來都對，只有這裡的機械式比對抓得到。別名一樣鍵進同一張表，因為
    /// <c>ReadDocs</c> 讓別名指向同一個 <c>Entry</c> 執行個體、寫進同一個字典。
    /// </remarks>
    [Fact]
    public void 名稱含別名不分大小寫跨檔不得重複()
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Record(string key, string fileName, string ownerName)
        {
            var label = $"{fileName}／{ownerName}";

            if (seen.TryGetValue(key, out var earlier))
            {
                Assert.Fail(
                    $"名稱 {key} 同時寫在 {earlier} 與 {label}：合併字典只鍵名稱、不分種類與大小寫，" +
                    "後面那筆會安靜蓋掉前面的說明");
            }

            seen[key] = label;
        }

        foreach (var file in DocFiles)
        {
            var root = LoadResource(file.FileName);

            foreach (var item in root["docs"].Items)
            {
                var name = item["name"].AsString();
                Record(name, file.FileName, name);

                foreach (var aliasValue in item["aliases"].Items)
                {
                    var alias = aliasValue.AsString();
                    Assert.True(alias.Length > 0, $"{file.FileName}／{name}：alias 是空字串");
                    Record(alias, file.FileName, name);
                }
            }
        }
    }

    /// <summary>內嵌表格上限四欄，每一列的欄數要與 columns 一致。</summary>
    [Fact]
    public void 內嵌表格形狀正確()
    {
        foreach (var file in DocFiles)
        {
            var root = LoadResource(file.FileName);

            foreach (var item in root["docs"].Items)
            {
                var name = item["name"].AsString();

                foreach (var reference in item["references"].Items)
                {
                    if (reference.Kind != JsonKind.Object)
                    {
                        continue;
                    }

                    var columns = reference["columns"].Items;
                    Assert.True(columns.Count > 0, $"{file.FileName}／{name}：內嵌表格沒有欄");
                    Assert.True(columns.Count <= SqlBuiltInReference.MaximumColumns, $"{file.FileName}／{name}：內嵌表格超過四欄");

                    Assert.All(reference["rows"].Items, row =>
                        Assert.Equal(columns.Count, row.Items.Count));
                }
            }
        }
    }

    /// <summary>字串引用（表格編號）一定要真的存在於 tables.json。</summary>
    [Fact]
    public void 字串引用的表格編號必須存在()
    {
        var tableIds = new HashSet<string>(TableIds(LoadResource("tables.json")["tables"]), StringComparer.Ordinal)
        {
            // datePart 由 SqlArgumentCatalog 在載入時組出來，不寫在 tables.json 裡
            // （見 SqlBuiltInDocCatalog.BuildDatePartTable），日期函式引用的是這個編號。
            "datePart"
        };

        foreach (var file in DocFiles)
        {
            var root = LoadResource(file.FileName);

            foreach (var item in root["docs"].Items)
            {
                var name = item["name"].AsString();

                foreach (var reference in item["references"].Items)
                {
                    if (reference.Kind == JsonKind.String)
                    {
                        Assert.Contains(reference.AsString(), tableIds);
                    }
                }
            }
        }
    }

    /// <summary>系統程序必須有 signature，且第一張表 title 為「參數」；語句必須有 signature。</summary>
    [Fact]
    public void 系統程序與語句必須有簽章()
    {
        foreach (var file in DocFiles.Where(f => f.RequiresSignature))
        {
            var root = LoadResource(file.FileName);

            Assert.All(root["docs"].Items, item =>
            {
                var name = item["name"].AsString();
                Assert.True(item["signature"].AsString().Length > 0, $"{file.FileName}／{name}：缺 signature");

                if (file.FileName == "system-procedures.json" && item["references"].Items.Count > 0)
                {
                    var first = item["references"].Items[0];
                    var title = first.Kind == JsonKind.String
                        ? LoadResource("tables.json")["tables"][first.AsString()]["title"].AsString()
                        : first["title"].AsString();
                    Assert.Equal("參數", title);
                }
            });
        }
    }

    /// <summary>函式與型別不得寫 signature：那兩種的唯一出處是既有目錄，寫了也不會被讀。</summary>
    [Fact]
    public void 函式與型別不得寫簽章欄位()
    {
        foreach (var file in DocFiles.Where(f => !f.RequiresSignature))
        {
            var root = LoadResource(file.FileName);

            Assert.All(root["docs"].Items, item =>
            {
                var name = item["name"].AsString();
                Assert.True(item["signature"].Kind == JsonKind.Null, $"{file.FileName}／{name}：不該寫 signature");
            });
        }
    }

    /// <summary>英文覆蓋檔引用的 name、example id 與表格位置都必須存在於來源。</summary>
    [Fact]
    public void 英文覆蓋檔的引用都存在()
    {
        foreach (var file in DocFiles)
        {
            var root = LoadResource(file.FileName);
            var overlay = LoadOverlay(file.FileName);

            var byName = root["docs"].Items.ToDictionary(item => item["name"].AsString(), item => item);

            foreach (var name in overlay.Names)
            {
                Assert.True(byName.TryGetValue(name, out var entry), $"{file.FileName}.en.json：{name} 不存在於來源");

                var exampleIds = new HashSet<string>(
                    entry!["examples"].Items.Select(example => example["id"].AsString()),
                    StringComparer.Ordinal);
                var referenceCount = entry["references"].Items.Count;

                foreach (var field in overlay[name].Names)
                {
                    if (field.StartsWith("examples.", StringComparison.Ordinal))
                    {
                        var id = field.Split('.')[1];
                        Assert.True(exampleIds.Contains(id), $"{file.FileName}.en.json：{name} 沒有範例 {id}");
                    }
                    else if (field.StartsWith("references.", StringComparison.Ordinal))
                    {
                        var index = int.Parse(field.Split('.')[1]);
                        Assert.True(index < referenceCount, $"{file.FileName}.en.json：{name} 沒有第 {index} 張內嵌表格");
                    }
                }
            }
        }

        var tables = LoadResource("tables.json")["tables"];
        var tablesOverlay = LoadOverlay("tables.json");

        foreach (var id in tablesOverlay.Names)
        {
            Assert.True(tables[id].Kind == JsonKind.Object, $"tables.en.json：{id} 不存在於來源");
        }
    }

    /// <summary>範例經詞法掃描後不能有未結束的字串或註解——貼進查詢視窗會整份變成字串的顏色。</summary>
    /// <remarks>
    /// 不自己寫字串或註解的略過邏輯（見 <c>docs/rules-parsing.md</c>），只呼叫既有的
    /// <see cref="SqlTokenizer"/> 並檢查它切出來的詞法單元有沒有收尾：字串收尾一定是
    /// 單引號，區塊註解收尾一定是 <c>*/</c>；沒有收尾時詞法單元會一路吃到文字結尾，
    /// 因此結尾字元對不上。
    /// </remarks>
    [Fact]
    public void 範例沒有未結束的字串或註解()
    {
        foreach (var file in DocFiles)
        {
            var root = LoadResource(file.FileName);

            foreach (var item in root["docs"].Items)
            {
                var name = item["name"].AsString();

                foreach (var example in item["examples"].Items)
                {
                    var sql = example["sql"].AsString();
                    var tokens = SqlTokenizer.TokenizeWithComments(sql);

                    foreach (var token in tokens)
                    {
                        if (token.Kind == SqlTokenKind.String)
                        {
                            Assert.True(
                                token.Text.EndsWith("'", StringComparison.Ordinal),
                                $"{file.FileName}／{name}／{example["id"].AsString()}：字串沒有收尾：{token.Text}");
                        }
                        else if (token.Kind == SqlTokenKind.Comment && token.Text.StartsWith("/*", StringComparison.Ordinal))
                        {
                            Assert.True(
                                token.Text.EndsWith("*/", StringComparison.Ordinal),
                                $"{file.FileName}／{name}／{example["id"].AsString()}：區塊註解沒有收尾");
                        }
                    }
                }
            }
        }
    }
}
