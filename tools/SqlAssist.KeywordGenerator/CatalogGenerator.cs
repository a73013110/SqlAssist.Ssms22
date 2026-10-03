using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlAssist.KeywordGenerator.Data;

namespace SqlAssist.KeywordGenerator;

/// <summary>產生器的輸入：路徑由 tools/Generate-Keywords.ps1 解析好再交過來。</summary>
public sealed class GeneratorOptions
{
    /// <summary>SSMS 那一份 ScriptDom；執行期綁到的必須是它。</summary>
    public string ScriptDomPath { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    public string CachePath { get; set; } = string.Empty;

    /// <summary>false 時不讀舊快取、全部重新剖析，結果照樣寫回。</summary>
    public bool UseCache { get; set; } = true;

    /// <summary>Core 的 statements.json：語句說明是片語的證據。</summary>
    public string StatementDocsPath { get; set; } = string.Empty;

    /// <summary>Core 的 functions.json：內建函式的名稱由函式目錄列，附加片語不收。</summary>
    public string FunctionDocsPath { get; set; } = string.Empty;
}

/// <summary>產生器的輸出管道；啟動器接到 Write-Host、Write-Warning、Write-Progress。progress 收到 null 是探測結束。</summary>
public sealed class GeneratorLog(Action<string> info, Action<string> warning, Action<string?> progress)
{
    public Action<string> Info { get; } = info;

    public Action<string> Warning { get; } = warning;

    public Action<string?> Progress { get; } = progress;
}

/// <summary>
/// 以 ScriptDom 產生 T-SQL 關鍵字目錄（SqlKeywordCatalog.Generated.cs）。關鍵字清單刻意不手寫，改由 Microsoft
/// 自己的剖析器推導，換版本重跑即可更新；每個階段都會自我驗證，不猜任何一個字。
/// </summary>
/// <remarks>
/// <para>一、取字面值：列舉 TSqlTokenType 的成員名稱，大寫後丟回 tokenizer；token 型別對得回原成員才採用。
/// 標點與字面值（Comma、HexLiteral…）自然對不回來，因此被排除。名稱含 camelCase 轉折的再試一次補底線的寫法，
/// 撈回 CURRENT_TIMESTAMP、IDENTITY_INSERT、TRY_CONVERT 這一類。</para>
/// <para>二、判保留字：「這個字當名字寫，剖析器接不接受」跟「它能出現在哪個位置」是兩回事，因此另外探測一次：
/// 把字塞進識別字的洞裡（SELECT ? FROM t、FROM ?、CREATE TABLE t (? int)…），被拒的就是插入時一定要加方括號的保留字。
/// 目錄裡有 13 個字是非保留字（APPLY、OUTPUT、ROWS、GO…），當欄位名寫完全合法，靠這一階段才不會被多加一層括號。</para>
/// <para>三、定位置：把關鍵字塞進樣板的洞裡剖析，依錯誤碼判定它在該位置合不合法：
/// 46005 必須是 X 卻發現 Y、46010 語法不正確、46014 只可存在於資料行層級、46001 剖析器內部錯誤 → 不合法；
/// 46029 出現未預期的檔案結尾 → 合法，只是語句還沒寫完。
/// 單一續尾會誤判——BACKUP 之後是檔案結尾、SELECT 之後卻是語法錯誤，兩者都合法。因此每個位置試一組續尾取聯集：
/// 任一組能過就算合法。非保留字另有一條：同一組續尾換成普通名稱也過的話，那一次只證明它能當名字，不算它屬於這個位置。
/// 需要手寫的只有 <see cref="PositionTemplates"/> 的樣板，每個關鍵字的分類全部由剖析器決定。
/// 每個樣板必須是分析器判得出、而且回報含該位置的文字——兩邊說的是同一個位置；樣板表隨產物輸出，Core 的測試逐條回驗。
/// 樣板都進不去的字產出為 None，執行期只在分析器也判不出位置時出現。</para>
/// <para>四、子句片語：見 <see cref="PhraseExplorer"/>。手寫的只有片語的尾巴（<see cref="ClausePhrases"/>）；
/// 片語表與探測文字一併輸出，Core 的測試逐條回驗。</para>
/// <para>五、寫完一項的字：NULL、CURRENT_USER、DESC 這種字本身就把前一格開的那一項寫完，之後的位置與識別字之後相同。
/// 判法：任一個樣板接上它就是完整的一句，而且語法樹裡以它結尾的是語句以外的片段（運算式、排序項、提示）——
/// BEGIN TRAN 的 TRAN 寫完的是語句本身。</para>
/// <para>六、寫完一句的字：第五階段排除的那一半，以它結尾的是語句本身（BREAK、COMMIT、TRAN）。另外記下前一格
/// 在哪些位置時那一句再也接不了語句開頭以外的東西——COMMIT 還接 TRAN，就不算。</para>
/// <para>產物要進版控。剖析要上百萬次，建置時不跑，手動執行、結果 commit 進去。</para>
/// </remarks>
public static class CatalogGenerator
{
    // Sort-Object 的字串比較：文化感知、不分大小寫，底線排在字母之前。產物裡關鍵字與片語字的順序都由它決定。
    private static readonly StringComparer SortOrder = StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true);
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    public static void Run(GeneratorOptions options, GeneratorLog log)
    {
        var assembly = typeof(TSqlParser).Assembly;

        if (!IgnoreCase.Equals(Path.GetFullPath(assembly.Location), Path.GetFullPath(options.ScriptDomPath)))
        {
            throw new InvalidOperationException($"探測器綁到的 ScriptDom 是 {assembly.Location}，不是 SSMS 那一份 {options.ScriptDomPath}。");
        }

        // 只看宣告與文件就判得出的錯，在剖析任何東西之前一次報完。
        var templates = PositionTemplates.All.ToDictionary(position => position.Position, position => position.Templates, IgnoreCase);
        PhraseDeclaration.Validate(ClausePhrases.All, templates);
        var docs = StatementDocs.Load(options.StatementDocsPath);
        var functions = FunctionDocs.LoadNames(options.FunctionDocsPath);

        var prober = new KeywordProber(RejectingErrorNumbers(assembly, log), options.CachePath, options.UseCache);
        log.Info($"ScriptDom {prober.ScriptDomVersion}（{prober.ParserName}）");

        // 中途失敗（驗證不過之類）也把算過的結果存回去，下次從這裡接著算。Ctrl+C 攔不到，靠探測器每兩分鐘一次的檢查點。
        try
        {
            var catalog = Generate(prober, assembly, templates, docs, functions, log);
            var output = Path.GetFullPath(options.OutputPath);
            File.WriteAllText(output, CatalogWriter.Write(catalog), new UTF8Encoding(false));
            log.Info($"已寫出 {output}");
        }
        catch
        {
            prober.SaveCache(false);
            throw;
        }

        prober.SaveCache(true);
        log.Info($"{prober.CacheSummary()}，快取：{prober.CachePath}");
    }

    private static CatalogData Generate(
        KeywordProber prober, Assembly assembly, IReadOnlyDictionary<string, string[]> templates, StatementDocs docs,
        IReadOnlyCollection<string> functions, GeneratorLog log)
    {
        // ---------------------------------------------------------------- 一、取字面值

        var keywords = prober.ReadLiterals().ToList();
        var lexerCount = SortUnique(keywords).Count;

        foreach (var supplement in KeywordSupplements.NonReserved)
        {
            if (keywords.Contains(supplement, IgnoreCase))
            {
                // 這個字已經升格成保留字了，補充清單該把它拿掉，否則會一直是死條目。
                log.Warning($"補充清單裡的 {supplement} 已經是保留字，可以移除。");
                continue;
            }

            keywords.Add(supplement);
        }

        keywords = SortUnique(keywords);
        var keywordArray = keywords.ToArray();
        log.Info($"字面值：{keywords.Count} 個關鍵字（詞法器認得的 {lexerCount} + 非保留字補充 {KeywordSupplements.NonReserved.Length}）");

        // ---------------------------------------------------------------- 二、判保留字

        var prefixes = KeywordSupplements.IdentifierTemplates.Select(template => template.Prefix).ToArray();
        var suffixes = KeywordSupplements.IdentifierTemplates.Select(template => template.Suffix).ToArray();
        var reserved = prober.RejectedAsIdentifiers(keywordArray, prefixes, suffixes).ToList();
        var nonReserved = keywords.Where(keyword => !reserved.Contains(keyword, IgnoreCase)).ToList();

        foreach (var supplement in KeywordSupplements.Reserved)
        {
            if (reserved.Contains(supplement, IgnoreCase))
            {
                log.Warning($"補充清單裡的 {supplement} 已經在關鍵字清單裡，可以移除。");
                continue;
            }

            if (prober.RejectedAsIdentifiers([supplement], prefixes, suffixes).Length == 0)
            {
                // 剖析器接受它當名字，加了括號只是多餘。
                log.Warning($"補充清單裡的 {supplement} 不需要方括號，可以移除。");
                continue;
            }

            reserved.Add(supplement);
        }

        reserved = SortUnique(reserved);
        var reservedArray = reserved.ToArray();
        log.Info($"保留字：{reserved.Count} 個必須加方括號；非保留字 {nonReserved.Count} 個可以直接寫：{string.Join(", ", nonReserved)}");

        // ------------------------------------------------------------------ 三、定位置

        var positionNames = PositionTemplates.All.Select(position => position.Position).ToArray();
        var templateArrays = PositionTemplates.All.Select(position => position.Templates).ToArray();
        var reservedSet = new HashSet<string>(reserved, IgnoreCase);
        var canBeName = keywordArray.Select(keyword => !reservedSet.Contains(keyword)).ToArray();
        var allowed = prober.ClassifyPositions(keywordArray, canBeName, templateArrays, Continuations.Keywords, Continuations.PlainName);
        var positions = new Dictionary<string, List<string>>(IgnoreCase);

        for (var index = 0; index < keywordArray.Length; index++)
        {
            positions[keywordArray[index]] = positionNames.Where((_, position) => allowed[index][position]).ToList();
        }

        for (var position = 0; position < positionNames.Length; position++)
        {
            log.Info(string.Format("  {0,-20} {1,3}", positionNames[position], allowed.Count(row => row[position])));
        }

        var orphans = keywords.Where(keyword => positions[keyword].Count == 0).ToList();

        if (orphans.Count > 0)
        {
            // None 的字只在分析器也判不出位置（Any）時出現。它真正的用法若落在分析器判得出的
            // 位置，使用者在那裡就打不出它——該補的是樣板，不是放寬過濾。
            log.Warning($"有 {orphans.Count} 個關鍵字不屬於任何位置，將以 None 產出（只在判不出位置時出現）：{string.Join(", ", orphans)}");
        }

        // ------------------------------------------------------------------ 四、子句片語

        // 候選字：關鍵字清單，加上 ScriptDom 產生程式碼時用的全部字串常數。後者正是剖析器
        // 用字串比對認的那些非保留字（QUOTED_IDENTIFIER、REBUILD、MATCHED…），但也混著大量
        // 與文法無關的字——不必事先挑，接不接得上由探測決定。
        var supporter = assembly.GetType("Microsoft.SqlServer.TransactSql.ScriptDom.CodeGenerationSupporter")
            ?? throw new InvalidOperationException("ScriptDom 裡找不到 CodeGenerationSupporter；子句片語的候選字只能從那裡取。");
        var supporterWords = supporter.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.IsLiteral)
            .Select(field => field.GetRawConstantValue())
            .OfType<string>()
            .Where(word => Regex.IsMatch(word, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.IgnoreCase))
            .Select(word => word.ToUpperInvariant());
        var pool = SortUnique(keywords.Concat(supporterWords)).ToArray();
        log.Info($"子句片語候選字：{pool.Length} 個");

        var explorer = new PhraseExplorer(prober, pool, reservedArray, keywords, positions, templates, Continuations.Phrases, log.Progress)
        {
            BuiltInFunctions = functions,
        };
        explorer.Explore(ClausePhrases.All);
        explorer.AddEvidence(ClausePhrases.All);
        explorer.AddStatementEvidence(docs.StatementNames, log.Info);
        explorer.AddDbccArguments(docs.DbccArguments);
        explorer.OpenEmptyNameSlots();
        log.Progress(null);

        PhraseMerging.ChainUniqueContinuations(explorer.Phrases);
        var phrases = PhraseMerging.MergePositions(explorer.Phrases.Values);
        var keywordSet = new HashSet<string>(keywords, IgnoreCase);
        var phraseWords = SortUnique(phrases.SelectMany(phrase => phrase.Words).Where(word => !keywordSet.Contains(word)));
        log.Info($"子句片語：{phrases.Count} 個，其中關鍵字清單以外的字 {phraseWords.Count} 個");
        log.Info("附加片語：" + string.Join(", ", explorer.Additive.Select(additive => $"{additive.After}({string.Join(", ", additive.Words)})")));

        // ------------------------------------------------------------ 五、寫完一項的字

        // NULL、CURRENT_USER 本身就是完整的運算元，DESC 寫完 ORDER BY 的一項：這些字之後的位置
        // 與識別字之後相同，由往回找到的子句決定。
        var itemEndings = prober.ItemEndings(keywordArray, canBeName, templateArrays, Continuations.PlainName);
        log.Info($"寫完一項的字：{string.Join(", ", itemEndings)}");

        // ------------------------------------------------------------ 六、寫完一句的字

        // 第五階段排除的那一半：BREAK、COMMIT、BEGIN TRAN 的 TRAN 寫完的是語句本身。分析器拿它們
        // 認語句的界線，前一格落在「收得乾淨」的位置時，之後直接是下一句的開頭。
        var statementEndings = prober.StatementEndings(
            keywordArray, canBeName, positionNames, templateArrays, allowed, reservedArray, Continuations.Keywords, Continuations.PlainName);
        log.Info($"寫完一句的字：{string.Join(", ", statementEndings.Select(ending => $"{ending.Word}({string.Join("|", ending.Closes)})"))}");

        // 那一格除了名稱還接不接得上別的字，要等片語全部補完才知道：CREATE DATABASE 之後的 ENCRYPTION 是
        // CREATE DATABASE ENCRYPTION KEY 那一條補的。
        var createdKinds = explorer.CreatedKinds.Select(kind => (
            kind,
            phrases.Any(phrase => IgnoreCase.Equals(phrase.Pattern, "CREATE " + kind) &&
                phrase.After.Contains("StatementStart", IgnoreCase) && phrase.Words.Count > 0),
            explorer.SchemaQualifiedKinds.Contains(kind))).ToList();

        return new CatalogData
        {
            ScriptDomVersion = prober.ScriptDomVersion,
            ParserName = prober.ParserName,
            NonReservedSupplementCount = KeywordSupplements.NonReserved.Length,
            Keywords = keywords.Select(keyword => new KeyValuePair<string, IReadOnlyList<string>>(keyword, positions[keyword])).ToList(),
            Templates = PositionTemplates.All,
            Reserved = reserved,
            ItemEndings = itemEndings,
            StatementEndings = statementEndings,
            Phrases = phrases,
            Additive = explorer.Additive,
            CreatedKinds = createdKinds,
        };
    }

    // 46010 = "'X' 附近的語法不正確"。出現在關鍵字結尾之前代表剖析器根本吃不下它。
    // 46005 = "必須是 X，但卻發現 Y"。ORDER BY a Lib_Reader 1 報的是這一條而不是 46010，
    //         不算進來的話任何名稱都「接受」，非保留字的 OFFSET 就分不出來。
    // 46014 = "Default 條件約束只可存在於資料行層級"。剖析器吃得下 CREATE TABLE t (DEFAULT
    //         卻另外報這一條，不算進來的話 DEFAULT 會被分到資料行定義的開頭。
    // 46001 = "剖析器內部錯誤"。剖析器在報錯的詞元上當掉、之後都沒檢查，與 46010 一樣是它過不去的地方。
    //         不算進來的話 WITHIN GROUP (GRAPH 在檔案結尾只報這一條、一個拒收都沒有，GRAPH 就接得上。
    //         算在它報的位置而不是整段作廢：換一條續尾剖析器照常走完的話，那個字仍然成立。
    // 46029 = "出現未預期的檔案結尾"，代表吃下去了、只是語句沒寫完，那是合法的。
    //
    // 另有一族訊息說「這個字不是這裡的選項」（{0} is not a WITH option for a procedure.）：
    // 選項名稱在文法上是任意識別字，認不認得留到之後才判。不算進來的話任何名稱都是合法的選項，
    // CREATE TRIGGER … WITH 之後的 ENCRYPTION 就分不出來。號碼不手寫，從剖析器的訊息資源撈。
    private static int[] RejectingErrorNumbers(Assembly assembly, GeneratorLog log)
    {
        var messages = new ResourceManager("Microsoft.SqlServer.TransactSql.ScriptDom.TSqlParserResource", assembly)
            .GetResourceSet(CultureInfo.InvariantCulture, true, true)
            ?? throw new InvalidOperationException("ScriptDom 裡找不到剖析器的訊息資源。");
        var optionRejections = messages.Cast<DictionaryEntry>()
            .Where(entry => entry.Key is string key && Regex.IsMatch(key, @"^SQL\d+Message$", RegexOptions.IgnoreCase) &&
                entry.Value is string text &&
                Regex.IsMatch(text, @"^(\{0\}|Option '\{0\}') is not a .*\b(option|hint|function)\b", RegexOptions.IgnoreCase))
            .Select(entry => int.Parse(Regex.Replace((string)entry.Key, @"\D", string.Empty), CultureInfo.InvariantCulture))
            .OrderBy(number => number)
            .ToList();

        if (optionRejections.Count == 0)
        {
            throw new InvalidOperationException("剖析器的訊息資源裡找不到「不是這裡的選項」那一族；資源名稱或文案變了。");
        }

        log.Info($"選項拒收訊息：{string.Join(", ", optionRejections)}");
        return [46001, 46005, 46010, 46014, .. optionRejections];
    }

    private static List<string> SortUnique(IEnumerable<string> words)
    {
        var result = new List<string>();

        foreach (var word in words.OrderBy(word => word, SortOrder))
        {
            if (result.Count == 0 || !SortOrder.Equals(result[result.Count - 1], word))
            {
                result.Add(word);
            }
        }

        return result;
    }
}
