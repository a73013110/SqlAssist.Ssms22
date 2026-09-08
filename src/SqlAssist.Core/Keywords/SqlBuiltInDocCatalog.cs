using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Json;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Keywords;

/// <summary>內建名稱的說明，供滑鼠停留提示與建議清單的說明面板共用。</summary>
/// <remarks>
/// 與建議清單分家是刻意的。<see cref="SqlFunctionCatalog.All"/> 在每一次開清單時
/// 都要交出上百個建議項，那條路徑只需要名稱與簽章；用途與範例是**只有真的要顯示
/// 時才有人看**的內容，因此放在內嵌 JSON 裡，第一次查詢才解析，之後查表 O(1)。
/// 把說明併進建議項等於讓每一次按鍵都扛著幾十 KB 的字串。
///
/// 名稱的涵蓋範圍比建議清單廣：<see cref="SqlFunctionCatalog.All"/> 會把與關鍵字
/// 重疊的名稱讓給關鍵字目錄，但 <c>CONVERT</c>、<c>LEFT</c> 這些正是使用者最常停上去
/// 問的字。提示不必為了 <c>LEFT JOIN</c> 讓開——那是清單才有的兩難。
///
/// 收的是六種名稱（見 <see cref="SqlBuiltInKind"/>），一行說明各自留在建議清單已經
/// 在用的那份目錄裡，這裡只負責接起來。<c>@@ROWCOUNT</c> 與 <c>NOLOCK</c> 停上去要問的
/// 與 <c>CONVERT</c> 是同一件事，答案卻分散在四個型別裡，讓呼叫端自己去問等於讓
/// 兩個表面各接一次。
/// </remarks>
public static class SqlBuiltInDocCatalog
{
    private const string ResourceName = "SqlAssist.Core.Keywords.BuiltInDocs.json";

    /// <summary>目前支援的資源版本。</summary>
    public const int CurrentVersion = 1;

    /// <summary>資料格的欄要繫結到真的屬性，索引子路徑在複製那條路上讀不出來。</summary>
    private const int MaximumColumns = SqlBuiltInReference.MaximumColumns;

    /// <summary>
    /// 由既有目錄組出來的對照表，資源以這個編號引用。
    /// </summary>
    /// <remarks>
    /// 15 個 datepart 的名稱與說明已經寫在 <see cref="SqlArgumentCatalog"/> 裡，
    /// 那是建議清單在用的同一份。抄進 JSON 的症狀是改了一邊另一邊沒改，
    /// 而兩邊都看得見。
    /// </remarks>
    private const string DatePartTableId = "datePart";

    /// <summary>提示最長的多字寫法是 <c>OPTIMIZE FOR UNKNOWN</c>，三個詞。</summary>
    private const int MaximumHintWords = 3;

    private sealed class Entry
    {
        public Entry(
            SqlBuiltInKind kind,
            string summary,
            string example,
            string docsUrl,
            IReadOnlyList<string> references)
        {
            Kind = kind;
            Summary = summary;
            Example = example;
            DocsUrl = docsUrl;
            References = references;
        }

        public SqlBuiltInKind Kind { get; }

        public string Summary { get; }

        public string Example { get; }

        public string DocsUrl { get; }

        /// <summary>要引用哪幾張對照表；順序就是分頁的順序。</summary>
        public IReadOnlyList<string> References { get; }
    }

    private sealed class Resource
    {
        public Resource(
            Dictionary<string, Entry> entries,
            Dictionary<string, SqlBuiltInReference> tables)
        {
            Entries = entries;
            Tables = tables;
        }

        public Dictionary<string, Entry> Entries { get; }

        public Dictionary<string, SqlBuiltInReference> Tables { get; }
    }

    private static readonly Lazy<Resource> Loaded = new(Load);

    private static Dictionary<string, Entry> Entries => Loaded.Value.Entries;

    /// <summary>上一次載入內嵌資源失敗的原因；成功時為 null。</summary>
    public static string? LastError { get; private set; }

    /// <summary>資源裡寫過說明的名稱。</summary>
    /// <remarks>
    /// 給測試反推用：名稱打錯字的症狀是那一筆安靜地永遠查不到，
    /// 而從既有目錄那邊反推是問不出來的——目錄不知道誰寫過說明。
    /// </remarks>
    public static IReadOnlyCollection<string> DocumentedNames => Entries.Keys;

    /// <summary>資源裡某一筆宣告的種類。</summary>
    /// <remarks>
    /// 同樣給測試反推用：種類寫錯的那一筆會安靜地貼不到任何名稱上，
    /// 而從名稱那邊是問不出來的——<c>YEAR</c> 在四份目錄裡可以有四個意思。
    /// </remarks>
    public static bool TryGetDocumentedKind(string name, out SqlBuiltInKind kind)
    {
        if (Entries.TryGetValue(name, out var entry))
        {
            kind = entry.Kind;
            return true;
        }

        kind = SqlBuiltInKind.Function;
        return false;
    }

    /// <summary>
    /// 停在文字某個識別字上時該顯示哪一份說明。
    /// </summary>
    /// <remarks>
    /// 判斷全在這裡而不在呼叫端：只看文字就決定得了的事不放在平台接線層，
    /// 而且這樣才測得到。
    ///
    /// 函式與型別有三道限制。<c>[CONVERT]</c> 是欄位名，<c>dbo.CONVERT</c> 是使用者自己的函式，
    /// 兩者都不是內建名稱——加了方括號或限定詞時，參考的長度必然大於名稱長度。
    ///
    /// 第三道是左括號，與自動大寫同一條規則（見 <c>SqlKeywordCase</c>）：
    /// <c>max(</c> 在 T-SQL 裡只能是呼叫，而 <c>SELECT year FROM t</c> 的 <c>year</c>
    /// 是資料行名稱，把它說成 <c>YEAR()</c> 是提示自己編的。沒有左括號的關鍵字一律不認，
    /// 否則 <c>CREATE TABLE</c> 的 <c>TABLE</c> 會被說成資料表變數的型別。
    ///
    /// 另外兩種名稱靠的不是左括號。全域變數看的是開頭那兩個小老鼠，位置一概不問；
    /// 提示與日期部分反過來，只有在 <c>WITH (…)</c>、<c>OPTION (…)</c> 與
    /// <c>DATEADD(</c> 的第一個引數裡才算，離開那幾個括號 <c>NOLOCK</c> 與 <c>YEAR</c>
    /// 都只是欄位名。順序上它們排在左括號那一關之前，否則 <c>WITH (INDEX(1))</c> 的
    /// <c>INDEX</c> 會因為後面那個左括號被當成一次函式呼叫。
    /// </remarks>
    public static bool TryGetAt(string? text, SqlIdentifierReference? reference, out SqlBuiltInDoc doc)
    {
        doc = null!;

        if (text is null || reference is null)
        {
            return false;
        }

        if (reference.Qualifier is not null || reference.Length != reference.Name.Length)
        {
            return false;
        }

        // 全域變數不必看位置：@@ 開頭的名稱在 T-SQL 裡只有這一種意思，
        // 而識別字掃描已經把字串與註解裡的文字擋在外面。
        if (reference.Name.StartsWith("@@", StringComparison.Ordinal))
        {
            return TryGet(reference.Name, SqlBuiltInKind.GlobalVariable, out doc);
        }

        // 提示與日期部分反過來，離開那幾個括號一律不算：NOLOCK 與 YEAR 在別處是欄位名。
        if (TryGetArgumentKind(text, reference, out var kind) &&
            TryGetArgument(text, reference, kind, out doc))
        {
            return true;
        }

        var next = SqlTrivia.Skip(text, reference.End, text.Length);
        var call = next < text.Length && text[next] == '(';

        if (!call && SqlKeywordCatalog.IsKeyword(reference.Name))
        {
            return false;
        }

        return TryGet(
            reference.Name,
            call ? SqlBuiltInKind.Function : SqlBuiltInKind.DataType,
            out doc);
    }

    /// <summary>
    /// 停留的位置是不是那幾個括號裡的封閉清單。
    /// </summary>
    /// <remarks>
    /// 位置問的是 <see cref="SqlArgumentPosition"/>，與建議清單在同一個位置換掉整份
    /// 清單的是同一支：兩邊對「這裡只有這幾個字合法」的認定不該有兩套。
    ///
    /// 先用一次線性比對擋掉不在清單裡的名稱，再花詞法分析。這條路掛在滑鼠移動的
    /// 軌跡上，而停上去的名稱絕大多數是欄位與資料表。
    /// </remarks>
    private static bool TryGetArgumentKind(
        string text,
        SqlIdentifierReference reference,
        out SqlBuiltInKind kind)
    {
        kind = SqlBuiltInKind.Function;

        if (!SqlArgumentCatalog.Contains(reference.Name))
        {
            return false;
        }

        if (!SqlArgumentPosition.TryResolve(
                SqlTokenizer.Tokenize(text, 0, reference.Start),
                out var target))
        {
            return false;
        }

        switch (target)
        {
            case CompletionTarget.DatePart:
                kind = SqlBuiltInKind.DatePart;
                return true;
            case CompletionTarget.TableHint:
                kind = SqlBuiltInKind.TableHint;
                return true;
            case CompletionTarget.QueryHint:
                kind = SqlBuiltInKind.QueryHint;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 查出這個位置上的提示或日期部分，含 <c>FORCE ORDER</c> 這種多字寫法。
    /// </summary>
    /// <remarks>
    /// 多字寫法只有停在第一個詞上認得出來：停留範圍圈得住的就是游標底下那一個詞，
    /// 而由後面那個詞往回認要先知道前面還有幾個詞——為了半個名稱把位置分析整個
    /// 搬過來不划算，何況圈起來的範圍還是只有半個名稱。
    ///
    /// 由長到短試，否則 <c>OPTIMIZE FOR UNKNOWN</c> 會先被 <c>OPTIMIZE FOR</c> 接走，
    /// 而那兩個提示說的是相反的事。
    /// </remarks>
    private static bool TryGetArgument(
        string text,
        SqlIdentifierReference reference,
        SqlBuiltInKind kind,
        out SqlBuiltInDoc doc)
    {
        var names = new List<string>(MaximumHintWords) { reference.Name };
        var end = reference.End;

        while (names.Count < MaximumHintWords)
        {
            var start = SqlTrivia.Skip(text, end, text.Length);

            if (SqlIdentifierScanner.FindAt(text, start) is not { } following ||
                following.Start != start ||
                following.Length != following.Name.Length)
            {
                break;
            }

            names.Add(names[names.Count - 1] + " " + following.Name);
            end = following.End;
        }

        for (var index = names.Count - 1; index >= 0; index--)
        {
            if (TryGet(names[index], kind, out doc))
            {
                return true;
            }
        }

        doc = null!;
        return false;
    }

    /// <summary>
    /// 查出一個內建名稱的說明。
    /// </summary>
    /// <param name="preferred">
    /// 名稱同時是函式與型別時（<c>CHAR</c>、<c>NCHAR</c>）照這個偏好決定。
    /// 只有 <see cref="SqlBuiltInKind.Function"/> 查不到時才退到型別——
    /// <c>DECLARE @t TABLE (</c> 與 <c>nvarchar(200)</c> 的左括號屬於型別。
    /// 反過來不成立：沒有左括號的 <c>year</c> 不能退回去當函式解釋。
    /// </param>
    /// <remarks>
    /// 大小寫不敏感。查得到簽章或說明其中之一就算命中：函式一定有簽章，提示與型別
    /// 那幾種一定有那一行說明，兩邊都不必等資源寫過才答得出來——一行簽章本身就已經
    /// 回答了「引數順序是什麼」，那正是 <c>CONVERT</c> 最常被停上去問的事。
    /// </remarks>
    public static bool TryGet(string? name, SqlBuiltInKind preferred, out SqlBuiltInDoc doc)
    {
        doc = null!;

        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (preferred == SqlBuiltInKind.Function &&
            SqlFunctionCatalog.TryGetSignature(name!, out var signature))
        {
            doc = Create(name!, SqlBuiltInKind.Function, signature, string.Empty);
            return doc.HasContent;
        }

        // 只有函式退到型別，其餘種類不互退：位置已經把話說完了，
        // 而 WITH (MAXDOP) 也答得出來只會是提示自己編的。
        var kind = preferred == SqlBuiltInKind.Function ? SqlBuiltInKind.DataType : preferred;

        if (!TryGetDescription(name!, kind, out var description))
        {
            return false;
        }

        doc = Create(name!, kind, string.Empty, description);
        return doc.HasContent;
    }

    /// <summary>一行說明向那一份既有目錄要；資源不再抄一次。</summary>
    /// <remarks>
    /// 抄一次的症狀是建議清單與提示各說一套，而且沒有任何徵兆——兩邊看起來都對。
    /// </remarks>
    private static bool TryGetDescription(string name, SqlBuiltInKind kind, out string description)
    {
        switch (kind)
        {
            case SqlBuiltInKind.GlobalVariable:
                return SqlGlobalVariableCatalog.TryGetDescription(name, out description);
            case SqlBuiltInKind.TableHint:
            case SqlBuiltInKind.QueryHint:
            case SqlBuiltInKind.DatePart:
                return SqlArgumentCatalog.TryGetDescription(name, kind, out description);
            default:
                return SqlDataTypeCatalog.TryGetDescription(name, out description);
        }
    }

    /// <param name="description">
    /// 那一份既有目錄寫的一行說明；函式傳空字串，它的用途只寫在資源裡。
    /// </param>
    private static SqlBuiltInDoc Create(
        string name,
        SqlBuiltInKind kind,
        string signature,
        string description)
    {
        // 資源那一筆的種類要對得上：YEAR 在日期部分與內建函式目錄裡各有一筆，
        // 把函式的範例貼到日期部分上是提示自己編的。
        var entry = Entries.TryGetValue(name, out var candidate) && candidate.Kind == kind
            ? candidate
            : null;

        var summary = entry?.Summary ?? string.Empty;

        return new SqlBuiltInDoc(
            name.ToUpperInvariant(),
            kind,
            signature,
            summary.Length > 0 ? summary : description,
            entry?.Example ?? string.Empty,
            entry?.DocsUrl ?? string.Empty,
            Resolve(entry, kind));
    }

    /// <summary>把引用的編號換成對照表；查不到的編號安靜略過。</summary>
    /// <remarks>
    /// 編號打錯字是建置期的錯，由 <c>SqlBuiltInDocCatalogTests</c> 守。執行期少一個
    /// 分頁遠好過在滑鼠停留的路徑上丟例外。
    /// </remarks>
    private static IReadOnlyList<SqlBuiltInReference>? Resolve(Entry? entry, SqlBuiltInKind kind)
    {
        if (entry is null || entry.References.Count == 0)
        {
            // 停在 ISO_WEEK 上要問的正是「還有哪些值可以填」，而那張表已經有了。
            return kind == SqlBuiltInKind.DatePart
                ? new[] { Loaded.Value.Tables[DatePartTableId] }
                : null;
        }

        var tables = Loaded.Value.Tables;
        var resolved = new List<SqlBuiltInReference>(entry.References.Count);

        foreach (var id in entry.References)
        {
            if (tables.TryGetValue(id, out var table))
            {
                resolved.Add(table);
            }
        }

        return resolved;
    }

    /// <summary>
    /// 讀內嵌資源。
    /// </summary>
    /// <remarks>
    /// 讀不到一律降級成空字典，<b>不</b>丟例外，理由與 <c>SqlSnippetDefaults</c> 相同：
    /// 這是建置期的錯，而執行期這條路掛在滑鼠移動的軌跡上，丟出去就是每停留一次
    /// 看到一次錯誤，而且 <see cref="Lazy{T}"/> 會把例外永久快取起來反覆重丟。
    /// 沒有說明只是提示少了幾行，其餘功能照常。
    /// </remarks>
    private static Resource Load()
    {
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var tables = new Dictionary<string, SqlBuiltInReference>(StringComparer.Ordinal)
        {
            [DatePartTableId] = BuildDatePartTable()
        };

        try
        {
            var assembly = typeof(SqlBuiltInDocCatalog).GetTypeInfo().Assembly;

            using var stream = assembly.GetManifestResourceStream(ResourceName);

            if (stream is null)
            {
                LastError = $"找不到內建說明資源：{ResourceName}";
                return new Resource(entries, tables);
            }

            using var reader = new StreamReader(stream);
            var root = JsonReader.Parse(reader.ReadToEnd());

            if (root["version"].AsInt32(CurrentVersion) != CurrentVersion)
            {
                LastError = $"內建說明資源的版本不是 {CurrentVersion}。";
                return new Resource(entries, tables);
            }

            ReadTables(root["tables"], tables);
            ReadDocs(root["docs"], entries);

            if (entries.Count == 0)
            {
                LastError = "內建說明資源沒有可用項目。";
            }
        }
        catch (Exception exception)
        {
            LastError = $"內建說明資源讀取失敗：{exception.Message}";
        }

        return new Resource(entries, tables);
    }

    private static void ReadTables(JsonValue node, Dictionary<string, SqlBuiltInReference> tables)
    {
        foreach (var id in node.Names)
        {
            var table = node[id];
            var columns = new List<string>(MaximumColumns);

            foreach (var column in table["columns"].Items)
            {
                if (columns.Count < MaximumColumns)
                {
                    columns.Add(column.AsString());
                }
            }

            if (columns.Count == 0)
            {
                continue;
            }

            var rows = new List<IReadOnlyList<string>>();

            foreach (var row in table["rows"].Items)
            {
                var cells = new string[columns.Count];

                for (var index = 0; index < cells.Length; index++)
                {
                    // 列短於欄數時補空字串：資源是人手寫的，少打一格不該讓整張表消失。
                    cells[index] = index < row.Items.Count ? row.Items[index].AsString() : string.Empty;
                }

                rows.Add(cells);
            }

            tables[id] = new SqlBuiltInReference(table["title"].AsString(id), columns, rows);
        }
    }

    private static void ReadDocs(JsonValue node, Dictionary<string, Entry> entries)
    {
        foreach (var item in node.Items)
        {
            var name = item["name"].AsString();

            if (name.Length == 0)
            {
                continue;
            }

            var references = new List<string>();

            foreach (var reference in item["references"].Items)
            {
                references.Add(reference.AsString());
            }

            entries[name] = new Entry(
                ParseKind(item["kind"].AsString()),
                item["summary"].AsString(),
                item["example"].AsString(),
                item["docsUrl"].AsString(),
                references);
        }
    }

    /// <summary>資源寫的種類；認不得的一律當函式，那是資源裡最多的一種。</summary>
    /// <remarks>
    /// 種類打錯字的症狀與名稱打錯字一樣——那一筆安靜地不再貼到任何名稱上，
    /// 由 <c>SqlBuiltInDocCatalogTests</c> 守。
    /// </remarks>
    private static SqlBuiltInKind ParseKind(string value) => value switch
    {
        "dataType" => SqlBuiltInKind.DataType,
        "tableHint" => SqlBuiltInKind.TableHint,
        "queryHint" => SqlBuiltInKind.QueryHint,
        "datePart" => SqlBuiltInKind.DatePart,
        "globalVariable" => SqlBuiltInKind.GlobalVariable,
        _ => SqlBuiltInKind.Function
    };

    /// <summary>datepart 的名稱與說明只有 <see cref="SqlArgumentCatalog"/> 一份。</summary>
    private static SqlBuiltInReference BuildDatePartTable()
    {
        var parts = SqlArgumentCatalog.DateParts;
        var rows = new List<IReadOnlyList<string>>(parts.Count);

        foreach (var part in parts)
        {
            rows.Add(new[] { part.DisplayText, part.Description });
        }

        return new SqlBuiltInReference(
            "datepart 名稱",
            new[] { "名稱", "說明" },
            rows);
    }

}
