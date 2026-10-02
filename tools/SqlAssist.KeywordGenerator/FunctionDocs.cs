using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SqlAssist.Core.Json;

namespace SqlAssist.KeywordGenerator;

/// <summary>內建函式說明（Core 的 functions.json）裡的函式名稱。</summary>
/// <remarks>
/// 說明涵蓋的名稱與 Core 的 <c>SqlFunctionCatalog.Names</c> 一致（Core 測試守），所以這一份就是「函式目錄列得出哪些字」。
/// 產生器不參照 Core，讀同一份資料。
/// </remarks>
internal static class FunctionDocs
{
    public static HashSet<string> LoadNames(string path)
    {
        var docs = JsonReader.Parse(File.ReadAllText(path, Encoding.UTF8))["docs"].Items;

        return new HashSet<string>(
            docs.Where(doc => StringComparer.OrdinalIgnoreCase.Equals(doc["kind"].AsString(), "function"))
                .Select(doc => doc["name"].AsString()),
            StringComparer.OrdinalIgnoreCase);
    }
}
