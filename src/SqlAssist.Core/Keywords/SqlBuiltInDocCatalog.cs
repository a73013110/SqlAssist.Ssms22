using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Json;
using SqlAssist.Core.Localization;
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
/// 收的是八種名稱（見 <see cref="SqlBuiltInKind"/>）。函式、型別、兩種提示、日期部分與
/// 全域變數的一行說明各自留在建議清單已經在用的那份目錄裡，這裡只負責接起來；系統程序
/// 與語句沒有那樣一份目錄，簽章與用途由資源自己當唯一出處（見
/// <c>Keywords/BuiltInDocs/system-procedures.json</c>、<c>statements.json</c>）。
/// <c>@@ROWCOUNT</c> 與 <c>NOLOCK</c> 停上去要問的與 <c>CONVERT</c> 是同一件事，答案卻
/// 分散在好幾個型別裡，讓呼叫端自己去問等於讓每個表面各接一次。
/// </remarks>
public static class SqlBuiltInDocCatalog
{
    private const string ResourceFolder = "SqlAssist.Core.Keywords.BuiltInDocs.";

    /// <summary>依種類拆開的資料檔；合併成一份目錄，名稱重複由測試擋下。</summary>
    private static readonly string[] DocFileNames =
    {
        "functions.json",
        "types-hints.json",
        "statements.json",
        "system-procedures.json"
    };

    /// <summary>目前支援的資源版本。</summary>
    public const int CurrentVersion = 2;

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

    /// <summary>
    /// 一筆 <c>references</c> 要嘛引用 <c>tables.json</c> 的共用編號，要嘛是內嵌在該筆
    /// 資料裡的一張表；哪一種留到真的要顯示（<see cref="Resolve"/>）才決定，讀檔時只記來源。
    /// </summary>
    private readonly struct ReferenceSource
    {
        private readonly string? _tableId;
        private readonly SqlBuiltInReference? _inline;

        public ReferenceSource(string tableId)
        {
            _tableId = tableId;
            _inline = null;
        }

        public ReferenceSource(SqlBuiltInReference inline)
        {
            _tableId = null;
            _inline = inline;
        }

        /// <summary>內嵌的表直接回傳；共用編號向 <paramref name="tables"/> 查，查不到安靜略過。</summary>
        public SqlBuiltInReference? Resolve(IReadOnlyDictionary<string, SqlBuiltInReference> tables)
        {
            if (_inline is not null)
            {
                return _inline;
            }

            return _tableId is not null && tables.TryGetValue(_tableId, out var table) ? table : null;
        }
    }

    private sealed class Entry
    {
        public Entry(
            SqlBuiltInKind kind,
            string summary,
            string signature,
            IReadOnlyList<SqlBuiltInExample> examples,
            string docsUrl,
            IReadOnlyList<ReferenceSource> references)
        {
            Kind = kind;
            Summary = summary;
            Signature = signature;
            Examples = examples;
            DocsUrl = docsUrl;
            References = references;
        }

        public SqlBuiltInKind Kind { get; }

        public string Summary { get; }

        /// <summary>系統程序與語句的寫法；其餘種類永遠是空字串（測試擋下寫了也沒用的欄位）。</summary>
        public string Signature { get; }

        public IReadOnlyList<SqlBuiltInExample> Examples { get; }

        public string DocsUrl { get; }

        /// <summary>要引用哪幾張對照表；順序就是分頁的順序。</summary>
        public IReadOnlyList<ReferenceSource> References { get; }
    }

    private sealed class Resource
    {
        public Resource(
            Dictionary<string, Entry> entries,
            Dictionary<string, SqlBuiltInReference> tables)
        {
            Entries = entries;
            Tables = tables;
            Statements = StatementVocabulary.From(entries);
        }

        public Dictionary<string, Entry> Entries { get; }

        public Dictionary<string, SqlBuiltInReference> Tables { get; }

        public StatementVocabulary Statements { get; }
    }

    /// <summary>
    /// 語句名稱（含別名）用到的字，以及最長的名稱有幾個字。
    /// </summary>
    /// <remarks>
    /// 從資料算出來，不另寫常數：以前「語句最多兩個字」是一個常數，補一筆
    /// <c>CREATE UNIQUE NONCLUSTERED INDEX</c> 別名就得記得去改它，忘了的症狀是那個別名永遠對不上。
    /// 字表同時是停留提示的第一道篩子——停上去的名稱絕大多數是欄位與資料表，不在字表裡的
    /// 不必花一次整段詞法分析。
    /// </remarks>
    private sealed class StatementVocabulary
    {
        private readonly HashSet<string> _words;

        private StatementVocabulary(HashSet<string> words, int maxWords)
        {
            _words = words;
            MaxWords = maxWords;
        }

        public int MaxWords { get; }

        public bool Contains(string word) => _words.Contains(word);

        /// <summary>沒加方括號的一個字，而且是某個語句名稱裡的字。</summary>
        public bool Contains(SqlToken token) =>
            token.Kind == SqlTokenKind.Identifier && !token.IsQuoted && _words.Contains(token.Value);

        public static StatementVocabulary From(IEnumerable<KeyValuePair<string, Entry>> entries)
        {
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var maxWords = 0;

            foreach (var pair in entries)
            {
                if (pair.Value.Kind != SqlBuiltInKind.Statement)
                {
                    continue;
                }

                var parts = pair.Key.Split(' ');
                words.UnionWith(parts);
                maxWords = Math.Max(maxWords, parts.Length);
            }

            return new StatementVocabulary(words, maxWords);
        }
    }

    /// <summary>依語言各解析一份：用途、範例與對照表疊上該語言的覆蓋檔。</summary>
    private static readonly SqlLanguageCache<Resource> Loaded = new(Load);

    private static Dictionary<string, Entry> Entries => Loaded.Current.Entries;

    /// <summary>語句字表；測試疊加的假資料一併算進來，否則疊上去的語句永遠過不了第一道篩子。</summary>
    private static StatementVocabulary Statements =>
        TestOverlay.Value is { } overlay
            ? StatementVocabulary.From(Entries.Concat(overlay))
            : Loaded.Current.Statements;

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
        if (TryGetEntry(name, out var entry))
        {
            kind = entry.Kind;
            return true;
        }

        kind = SqlBuiltInKind.Function;
        return false;
    }

    /// <summary><see cref="TestOverlay"/> 疊加時的初始底稿；一律是空的。</summary>
    private static readonly Dictionary<string, Entry> EmptyOverlay = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>測試專用的疊加資料；不影響正式載入的 <see cref="Entries"/>，見 <see cref="UseTestEntries"/>。</summary>
    private static readonly AsyncLocal<Dictionary<string, Entry>?> TestOverlay = new();

    /// <summary>查一個名稱：先問測試疊加的那份，再問正式載入的資源。</summary>
    private static bool TryGetEntry(string name, out Entry entry)
    {
        if (TestOverlay.Value is { } overlay && overlay.TryGetValue(name, out entry!))
        {
            return true;
        }

        return Entries.TryGetValue(name, out entry!);
    }

    /// <summary>
    /// 測試專用：把一段 <c>docs</c> 陣列的 JSON 疊到目前的資源之上，不寫入正式 JSON。
    /// </summary>
    /// <remarks>
    /// 階段 3 的 <c>statements.json</c>／<c>system-procedures.json</c> 還是空的，2a 的辨識
    /// 測試需要幾筆假資料才能走完整條 <see cref="TryGetAt"/> 路徑（含 <c>aliases</c>）。
    /// 用 <see cref="AsyncLocal{T}"/> 疊加而不是直接改 <see cref="Entries"/>：測試平行執行時
    /// 各自的疊加互不影響，範圍與 <see cref="SqlAssist.Core.Localization.SqlText.Use"/> 是同一個
    /// 模式（含其中 await 的後續）。走的是真正的 <see cref="ReadDocs"/>，別名與格式規則不必
    /// 另外重寫一次。<see cref="DocumentedNames"/> 刻意不含疊加的假資料：那份是給「整份目錄都
    /// 寫過說明」這種測試在問，不該被暫時疊加的假資料污染。
    /// </remarks>
    internal static IDisposable UseTestEntries(string docsJson)
    {
        if (docsJson is null)
        {
            throw new ArgumentNullException(nameof(docsJson));
        }

        var previous = TestOverlay.Value;
        var merged = new Dictionary<string, Entry>(previous ?? EmptyOverlay, StringComparer.OrdinalIgnoreCase);

        ReadDocs(JsonReader.Parse(docsJson), merged, SqlTextOverlay.Empty);

        TestOverlay.Value = merged;
        return new RestoreTestOverlay(previous);
    }

    private sealed class RestoreTestOverlay : IDisposable
    {
        private readonly Dictionary<string, Entry>? _previous;
        private bool _disposed;

        public RestoreTestOverlay(Dictionary<string, Entry>? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            TestOverlay.Value = _previous;
        }
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
    ///
    /// 系統程序與語句另有兩道規則，見下方兩支 remarks：系統程序不看左括號、不看位置，
    /// 只看限定字；語句不看左括號，只看名稱落不落在一句開頭那串字裡。
    /// 兩者都要在函式／型別的左括號早退判斷之前先問，否則 <c>EXEC sp_x</c> 這種沒有
    /// 左括號的關鍵字會被「沒有左括號的關鍵字一律不認」擋下。
    /// </remarks>
    public static bool TryGetAt(string? text, SqlIdentifierReference? reference, out SqlBuiltInDoc doc)
    {
        doc = null!;

        if (text is null || reference is null)
        {
            return false;
        }

        // 系統程序要在「限定字早退」之前先問：sys.sp_executesql、master.sys.sp_help、
        // master..sp_help 都有限定字，晚一步問就先被下面那條擋掉了。
        if (TryGetSystemProcedureAt(reference, out doc))
        {
            return true;
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

        // 語句（EXEC、MERGE、BULK INSERT…）要在左括號早退之前問：這些關鍵字絕大多數
        // 後面沒有左括號，晚一步問就被下面「沒有左括號的關鍵字一律不認」擋掉了。
        if (Statements.Contains(reference.Name) && TryGetStatementAt(text, reference.End, out doc))
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
    /// 系統程序：名稱在說明目錄裡，且限定字為空、<c>sys</c>、<c>master.sys</c> 或
    /// <c>master..</c>（大小寫不分，方括號寫法一樣認，例如 <c>[sys].[sp_executesql]</c>）；
    /// 位置不限——EXEC 之後、<c>INSERT … EXEC</c> 之後、批次第一句都算。
    /// </summary>
    /// <remarks>
    /// SQL Server 解析 <c>sp_</c> 開頭的名稱本來就先找系統那一份，不必看游標落在哪裡，
    /// 這一條與函式／型別「沒有左括號一律不認」是兩套規則，各自的判準見
    /// <see cref="IsSystemProcedureQualifier"/>。
    /// </remarks>
    private static bool TryGetSystemProcedureAt(SqlIdentifierReference reference, out SqlBuiltInDoc doc)
    {
        doc = null!;

        var path = reference.Path;
        var qualifierSlots = path?.QualifierSlotCount ?? 0;

        if (qualifierSlots == 0)
        {
            // 沒有限定字：名稱本身不能加方括號，否則跟資料行同名分不出來——
            // 與函式、型別同一條規則（[CONVERT] 是欄位名，不是內建名稱）。
            if (reference.Length != reference.Name.Length)
            {
                return false;
            }
        }
        else if (!IsSystemProcedureQualifier(path!))
        {
            return false;
        }

        return TryGet(reference.Name, SqlBuiltInKind.SystemProcedure, out doc);
    }

    /// <summary>
    /// 限定字要嘛整個省略，要嘛是 <c>sys</c>、<c>master.sys</c>、<c>master..</c>：
    /// 沒有連結伺服器，資料庫沒寫或是 <c>master</c>，結構描述沒寫或是 <c>sys</c>。
    /// <c>dbo.sp_x</c>（結構描述不是 sys）、<c>otherdb.sys.x</c>（資料庫不是 master）都不算。
    /// </summary>
    private static bool IsSystemProcedureQualifier(SqlObjectPath path)
    {
        if (path.ServerName is not null)
        {
            return false;
        }

        if (path.SchemaName is not null && !string.Equals(path.SchemaName, "sys", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.DatabaseName is null || string.Equals(path.DatabaseName, "master", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 語句：名稱落在一句開頭那一串字裡，而那串字由長到短對得上一筆語句說明
    /// （<c>MERGE</c>、<c>BULK INSERT</c>、<c>CREATE UNIQUE INDEX</c>）。
    /// </summary>
    /// <param name="wordEnd">要問的那個字在 <paramref name="text"/> 裡的結尾。</param>
    /// <remarks>
    /// 停留提示與建議清單是同一條規則：清單把候選字接在游標前的文字後面再問一次
    /// （<see cref="TryGetStatementFor"/>），所以「選到它按向右鍵」與「寫下去之後停上去」
    /// 一定是同一個答案。以前清單只比名稱，<c>ALTER TABLE t </c> 之後選到 <c>MERGE</c>
    /// 也開出 MERGE 陳述式的說明。
    ///
    /// 開頭由 <see cref="SqlStatementBoundaries.IsStatementHead"/> 判，往回找最多
    /// 「最長的名稱有幾個字」那麼遠，中間每個字都要是語句名稱裡的字——<c>ALTER TABLE t MERGE</c>
    /// 在 <c>t</c> 就斷了。從開頭往後併字、由長到短試，對上的名稱要蓋到問的那個字：停在
    /// <c>CREATE INDEX</c> 的 <c>INDEX</c> 上一樣認得，<c>CREATE UNIQUE</c> 的 <c>UNIQUE</c> 則否。
    /// 修飾字的組合是資料（<c>aliases</c>），這裡不另寫一份修飾字名單。
    ///
    /// 開頭後面直接接 <c>AS</c> 的不是語句：<c>EXECUTE AS USER = '…'</c>、<c>WITH EXECUTE AS OWNER</c>
    /// 是切換執行身分的敘述，不是呼叫程序的 EXEC，分辨的線索只有這一個。
    /// </remarks>
    private static bool TryGetStatementAt(string text, int wordEnd, out SqlBuiltInDoc doc)
    {
        doc = null!;

        var vocabulary = Statements;
        var tokens = SqlTokenizer.Tokenize(text, 0, wordEnd);
        var word = tokens.Count - 1;
        var boundaries = new SqlStatementBoundaries(text, tokens);
        var head = -1;

        for (var index = word; index >= 0 && word - index < vocabulary.MaxWords && vocabulary.Contains(tokens[index]); index--)
        {
            if (boundaries.IsStatementHead(index) || IsInsertExecTarget(tokens, index))
            {
                head = index;
                break;
            }
        }

        if (head < 0)
        {
            return false;
        }

        var names = CollectMultiWordNames(text, tokens[head].Value, tokens[head].End, vocabulary.MaxWords);

        if (names.Count > 1 && names[1].EndsWith(" AS", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var index = names.Count - 1; index >= word - head; index--)
        {
            if (TryGet(names[index], SqlBuiltInKind.Statement, out doc))
            {
                return true;
            }
        }

        doc = null!;
        return false;
    }

    /// <summary>
    /// 建議清單的關鍵字候選：這個字寫在 <paramref name="position"/> 的話，會不會落在某個語句
    /// 開頭那串字裡。
    /// </summary>
    /// <param name="text">整份指令碼；只讀 <paramref name="position"/> 之前那一段。</param>
    /// <param name="position">正在打的那個字的起點。</param>
    /// <param name="candidate">候選的顯示文字。</param>
    /// <remarks>
    /// 判準與停留提示是同一支（<see cref="TryGetStatementAt"/>），差別只在文字是假設的：
    /// 候選字還沒寫進去，所以接在游標前面再問。清單建好時位置已經分析過，但那一份回答的是
    /// 「這裡可以出現哪些字」，不是「這個字在這裡是不是一句的開頭」——<c>MERGE</c> 在
    /// <c>ALTER TABLE t </c> 之後也列得出來。
    /// </remarks>
    public static bool TryGetStatementFor(string? text, int position, string? candidate, out SqlBuiltInDoc doc)
    {
        doc = null!;

        if (text is null || string.IsNullOrEmpty(candidate) || position < 0 || position > text.Length ||
            !Statements.Contains(candidate!.Substring(candidate.LastIndexOf(' ') + 1)))
        {
            return false;
        }

        var probe = text.Substring(0, position) + candidate;
        return TryGetStatementAt(probe, probe.Length, out doc);
    }

    /// <summary>
    /// <paramref name="index"/> 的 EXEC／EXECUTE 前面是 <c>INSERT [INTO] target</c>：
    /// 目標名稱寫完之後直接呼叫程序，把結果集塞進那張表，這一句本身不是以 EXEC 開頭，
    /// 但 EXEC 在這裡一樣要算數（<c>INSERT #t EXEC sp_x</c>）。
    /// </summary>
    /// <remarks>
    /// 目標名稱那一格不能是關鍵字（<c>SELECT EXEC</c> 這種無效寫法擋在這裡），避免
    /// 誤判成 INSERT 的目標。留著這支的原因是實測反例：<c>INSERT INTO dbo.Lib_Tag EXEC …</c>
    /// 這種帶 <c>INTO</c> 與限定名稱的寫法，單靠 <see cref="SqlStatementBoundaries.IsStatementHead"/>
    /// 認不出來（位置分析不知道「這裡其實是 INSERT 目標寫完」），拿掉這支會讓這種合法寫法
    /// 查不到語句說明。
    /// </remarks>
    private static bool IsInsertExecTarget(IReadOnlyList<SqlToken> tokens, int index)
    {
        var before = index - 1;

        if (before < 0)
        {
            return false;
        }

        var target = tokens[before];

        if (target.Kind != SqlTokenKind.Identifier || (!target.IsQuoted && SqlKeywordCatalog.IsKeyword(target.Value)))
        {
            return false;
        }

        var previous = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, before) - 1;

        if (previous >= 0 && tokens[previous].IsKeyword("INTO"))
        {
            previous--;
        }

        return previous >= 0 && tokens[previous].IsKeyword("INSERT");
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
    /// 由長到短試，否則 <c>OPTIMIZE FOR UNKNOWN</c> 會先被 <c>OPTIMIZE FOR</c> 接走，
    /// 而那兩個提示說的是相反的事；併詞的規則見 <see cref="CollectMultiWordNames"/>。
    /// </remarks>
    private static bool TryGetArgument(
        string text,
        SqlIdentifierReference reference,
        SqlBuiltInKind kind,
        out SqlBuiltInDoc doc)
    {
        var names = CollectMultiWordNames(text, reference, MaximumHintWords);

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
    /// 由 <paramref name="first"/> 往後併詞，最多 <paramref name="maxWords"/> 個，供多字寫法
    /// （<c>OPTIMIZE FOR UNKNOWN</c>、<c>CREATE UNIQUE INDEX</c>）由長到短試。
    /// </summary>
    /// <remarks>
    /// 提示只從停留的那個詞往後併：提示停在第二個詞上認不出來，為了半個名稱把位置分析
    /// 整個搬過來不划算。語句不同，一句的開頭本來就要問位置分析，所以從開頭那個詞併起
    /// （見 <see cref="TryGetStatementAt"/>）。
    /// </remarks>
    private static List<string> CollectMultiWordNames(string text, SqlIdentifierReference reference, int maxWords) =>
        CollectMultiWordNames(text, reference.Name, reference.End, maxWords);

    private static List<string> CollectMultiWordNames(string text, string first, int firstEnd, int maxWords)
    {
        var names = new List<string>(maxWords) { first };
        var end = firstEnd;

        while (names.Count < maxWords)
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

        return names;
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
    /// 大小寫不敏感。函式、型別、提示與日期部分那幾種一定有一行說明或簽章，兩邊都不必
    /// 等資源寫過才答得出來；系統程序與語句沒有另一份目錄可退，資源本身就是唯一出處，
    /// 查得到就有內容。
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

        // 系統程序與語句沒有另一份目錄可退：JSON 資源本身就是唯一出處，
        // 查得到就有內容，查不到就是還沒寫過（見 SqlBuiltInDoc 類別備註）。
        if (kind == SqlBuiltInKind.SystemProcedure || kind == SqlBuiltInKind.Statement)
        {
            doc = Create(name!, kind, string.Empty, string.Empty);
            return doc.HasContent;
        }

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
    /// 那一份既有目錄寫的一行說明；函式、系統程序與語句傳空字串，它們的用途只寫在資源裡。
    /// </param>
    private static SqlBuiltInDoc Create(
        string name,
        SqlBuiltInKind kind,
        string signature,
        string description)
    {
        // 資源那一筆的種類要對得上：YEAR 在日期部分與內建函式目錄裡各有一筆，
        // 把函式的範例貼到日期部分上是提示自己編的。
        var entry = TryGetEntry(name, out var candidate) && candidate.Kind == kind
            ? candidate
            : null;

        var summary = entry?.Summary ?? string.Empty;
        var finalSignature = signature.Length > 0 ? signature : entry?.Signature ?? string.Empty;

        return new SqlBuiltInDoc(
            name.ToUpperInvariant(),
            kind,
            finalSignature,
            summary.Length > 0 ? summary : description,
            entry?.Examples ?? Array.Empty<SqlBuiltInExample>(),
            entry?.DocsUrl ?? string.Empty,
            Resolve(entry, kind));
    }

    /// <summary>把引用換成對照表：共用編號向 tables.json 查，內嵌的表直接拿來用。</summary>
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
                ? new[] { Loaded.Current.Tables[DatePartTableId] }
                : null;
        }

        var tables = Loaded.Current.Tables;
        var resolved = new List<SqlBuiltInReference>(entry.References.Count);

        foreach (var source in entry.References)
        {
            if (source.Resolve(tables) is { } table)
            {
                resolved.Add(table);
            }
        }

        return resolved;
    }

    /// <summary>
    /// 讀所有內嵌資源並合併成一份：共用的 <c>tables.json</c>，加上依種類拆開的四份資料檔。
    /// </summary>
    /// <remarks>
    /// 每一份各自讀、各自可能失敗；讀不到一律降級成空字典，<b>不</b>丟例外，理由與
    /// <c>SqlSnippetDefaults</c> 相同：這是建置期的錯，而執行期這條路掛在滑鼠移動的
    /// 軌跡上，丟出去就是每停留一次看到一次錯誤，而且 <see cref="Lazy{T}"/> 會把例外
    /// 永久快取起來反覆重丟。沒有說明只是提示少了幾行，其餘功能照常。覆蓋檔讀不到時
    /// 整份退回來源語言，理由相同。<see cref="LastError"/> 只留第一個失敗的原因，
    /// 其餘資料檔照樣繼續讀——一份寫壞不該連帶把其他四份也擋下來。
    /// </remarks>
    [Localizable(false)]
    private static Resource Load(SqlLanguage language)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var tables = new Dictionary<string, SqlBuiltInReference>(StringComparer.Ordinal)
        {
            [DatePartTableId] = BuildDatePartTable()
        };

        var assembly = typeof(SqlBuiltInDocCatalog).GetTypeInfo().Assembly;
        var error = LoadTables(assembly, language, tables);

        foreach (var fileName in DocFileNames)
        {
            error ??= LoadDocs(assembly, fileName, language, entries);
        }

        if (error is null && entries.Count == 0)
        {
            error = "內建說明資源沒有可用項目。";
        }

        LastError = error;
        return new Resource(entries, tables);
    }

    [Localizable(false)]
    private static string? LoadTables(Assembly assembly, SqlLanguage language, Dictionary<string, SqlBuiltInReference> tables)
    {
        const string resourceName = ResourceFolder + "tables.json";

        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);

            if (stream is null)
            {
                return $"找不到內建說明資源：{resourceName}";
            }

            using var reader = new StreamReader(stream);
            var root = JsonReader.Parse(reader.ReadToEnd());

            if (root["version"].AsInt32(CurrentVersion) != CurrentVersion)
            {
                return $"內建說明資源的版本不是 {CurrentVersion}：{resourceName}";
            }

            var overlay = SqlTextOverlay.Load(assembly, resourceName, language);
            ReadTables(root["tables"], tables, overlay);
            return null;
        }
        catch (Exception exception)
        {
            return $"內建說明資源讀取失敗：{resourceName}：{exception.Message}";
        }
    }

    [Localizable(false)]
    private static string? LoadDocs(
        Assembly assembly,
        string fileName,
        SqlLanguage language,
        Dictionary<string, Entry> entries)
    {
        var resourceName = ResourceFolder + fileName;

        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);

            if (stream is null)
            {
                return $"找不到內建說明資源：{resourceName}";
            }

            using var reader = new StreamReader(stream);
            var root = JsonReader.Parse(reader.ReadToEnd());

            if (root["version"].AsInt32(CurrentVersion) != CurrentVersion)
            {
                return $"內建說明資源的版本不是 {CurrentVersion}：{resourceName}";
            }

            var overlay = SqlTextOverlay.Load(assembly, resourceName, language);
            ReadDocs(root["docs"], entries, overlay);
            return null;
        }
        catch (Exception exception)
        {
            return $"內建說明資源讀取失敗：{resourceName}：{exception.Message}";
        }
    }

    /// <summary>
    /// 讀一張對照表：<c>tables.json</c> 的共用表與內嵌在某一筆資料裡的表走同一支，
    /// 差別只在覆蓋檔的鍵路徑（<paramref name="overlayId"/>／<paramref name="keyPrefix"/>）。
    /// </summary>
    private static SqlBuiltInReference? BuildReferenceTable(
        JsonValue table,
        string overlayId,
        string keyPrefix,
        string fallbackTitle,
        SqlTextOverlay overlay)
    {
        var columns = new List<string>(MaximumColumns);

        foreach (var column in table["columns"].Items)
        {
            if (columns.Count < MaximumColumns)
            {
                columns.Add(overlay.Apply(overlayId, keyPrefix + "columns." + columns.Count, column.AsString()));
            }
        }

        if (columns.Count == 0)
        {
            return null;
        }

        var rows = new List<IReadOnlyList<string>>();

        foreach (var row in table["rows"].Items)
        {
            var cells = new string[columns.Count];

            for (var index = 0; index < cells.Length; index++)
            {
                // 列短於欄數時補空字串：資源是人手寫的，少打一格不該讓整張表消失。
                cells[index] = index < row.Items.Count
                    ? overlay.Apply(overlayId, keyPrefix + "rows." + rows.Count + "." + index, row.Items[index].AsString())
                    : string.Empty;
            }

            rows.Add(cells);
        }

        return new SqlBuiltInReference(
            overlay.Apply(overlayId, keyPrefix + "title", table["title"].AsString(fallbackTitle)),
            columns,
            rows);
    }

    /// <remarks>
    /// 覆蓋檔的編號就是表的編號本身（不再加 <c>tables.</c> 前置詞——這份表現在自己
    /// 一個檔案，前置詞留給還在 <c>BuiltInDocs.json</c> 裡混著的年代），欄位是
    /// <c>title</c>、<c>columns.&lt;欄&gt;</c> 與 <c>rows.&lt;列&gt;.&lt;欄&gt;</c>。
    /// </remarks>
    private static void ReadTables(
        JsonValue node,
        Dictionary<string, SqlBuiltInReference> tables,
        SqlTextOverlay overlay)
    {
        foreach (var id in node.Names)
        {
            if (BuildReferenceTable(node[id], id, string.Empty, id, overlay) is { } table)
            {
                tables[id] = table;
            }
        }
    }

    /// <remarks>覆蓋檔的編號是 <c>name</c>，欄位是 <c>summary</c>、
    /// <c>examples.&lt;段落編號&gt;.title</c>、<c>examples.&lt;段落編號&gt;.sql</c>，
    /// 內嵌表格是 <c>references.&lt;索引&gt;.title</c> 這一組。<c>aliases</c>（例如
    /// <c>EXEC</c> 條目的 <c>["EXECUTE"]</c>）不需要覆蓋檔鍵：別名本身不翻譯，指向的是
    /// 同一個 <see cref="Entry"/> 執行個體，跟著原文的說明一起套用覆蓋檔。</remarks>
    private static void ReadDocs(JsonValue node, Dictionary<string, Entry> entries, SqlTextOverlay overlay)
    {
        foreach (var item in node.Items)
        {
            var name = item["name"].AsString();

            if (name.Length == 0)
            {
                continue;
            }

            var entry = new Entry(
                ParseKind(item["kind"].AsString()),
                overlay.Apply(name, "summary", item["summary"].AsString()),
                item["signature"].AsString(),
                ReadExamples(item["examples"], name, overlay),
                item["docsUrl"].AsString(),
                ReadReferences(item["references"], name, overlay));

            entries[name] = entry;

            // 別名（EXEC／EXECUTE 這種同一份說明有兩種寫法）指向同一個 Entry 執行個體，
            // 不是複製一份：抄一份的症狀是改了一邊另一邊沒改，兩邊看起來卻都對。
            foreach (var alias in item["aliases"].Items)
            {
                var aliasName = alias.AsString();

                if (aliasName.Length > 0)
                {
                    entries[aliasName] = entry;
                }
            }
        }
    }

    /// <summary>一筆的 2–5 段範例；還沒寫的名稱回傳空集合。</summary>
    private static IReadOnlyList<SqlBuiltInExample> ReadExamples(JsonValue node, string ownerName, SqlTextOverlay overlay)
    {
        if (node.Items.Count == 0)
        {
            return Array.Empty<SqlBuiltInExample>();
        }

        var examples = new List<SqlBuiltInExample>(node.Items.Count);

        foreach (var item in node.Items)
        {
            var id = item["id"].AsString();

            if (id.Length == 0)
            {
                continue;
            }

            examples.Add(new SqlBuiltInExample(
                id,
                overlay.Apply(ownerName, "examples." + id + ".title", item["title"].AsString()),
                overlay.Apply(ownerName, "examples." + id + ".sql", item["sql"].AsString())));
        }

        return examples;
    }

    /// <summary>
    /// <c>references</c> 陣列同時接受字串（<c>tables.json</c> 的共用編號）與物件
    /// （直接寫在這一筆裡的 <c>{title, columns, rows}</c>）；哪一種留到顯示時才解析（見
    /// <see cref="ReferenceSource"/>），這裡只記來源，字串打錯字或物件缺欄位安靜略過。
    /// </summary>
    private static List<ReferenceSource> ReadReferences(JsonValue node, string ownerName, SqlTextOverlay overlay)
    {
        var references = new List<ReferenceSource>(node.Items.Count);

        for (var index = 0; index < node.Items.Count; index++)
        {
            var item = node.Items[index];

            if (item.Kind == JsonKind.String)
            {
                references.Add(new ReferenceSource(item.AsString()));
                continue;
            }

            if (item.Kind == JsonKind.Object &&
                BuildReferenceTable(item, ownerName, "references." + index + ".", string.Empty, overlay) is { } inline)
            {
                references.Add(new ReferenceSource(inline));
            }
        }

        return references;
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
        "systemProcedure" => SqlBuiltInKind.SystemProcedure,
        "statement" => SqlBuiltInKind.Statement,
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
            KeywordText.DatePartTableTitle,
            new[] { CommonText.Name, CommonText.Description },
            rows);
    }

}
