using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SqlAssist.KeywordGenerator.Data;

namespace SqlAssist.KeywordGenerator;

/// <summary>
/// 第四階段：子句片語。SET 選項、ALTER INDEX 的動作、FOR XML 的模式這些字在文法上不是關鍵字
/// （ScriptDom 把它們掃成識別字），前三個階段撈不到。這一階段換一個問法：每個片語是一段「游標前面的尾巴」
/// （SET STATISTICS、ALTER INDEX {name} ON {name}），候選字取 ScriptDom 內部 CodeGenerationSupporter 的
/// 所有字串常數加上關鍵字清單，以與第三階段相同的規則（普通名稱過不了而它過得了）決定哪些字接得上。
/// 普通名稱在每一組續尾都過不了的片語是「封閉」的：那裡除了這幾個字沒有別的東西是對的。
/// 片語也記下它前面那一格的位置（After），探測借用第三階段的樣板，執行期以同一個位置分析回驗：
/// 一句開頭的 SET 與 UPDATE t SET 的 SET 是同一條尾巴、不同的意思。
/// </summary>
/// <remarks>集合比較一律不分大小寫，只有「這一格已經有這個字」分大小寫：手寫值與探到的字大小寫不同時兩者都留，改了會改到產物。</remarks>
internal sealed class PhraseExplorer
{
    private const string StatementStart = "StatementStart";

    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex NameLiteral = new(@"^([A-Za-z_]\w*|,|\()$", RegexOptions.IgnoreCase);
    private static readonly Regex StartsWord = new("^[A-Za-z_]", RegexOptions.IgnoreCase);
    private static readonly Regex OperandEnd = new(@"(\{value\}|\{name\}|=)$", RegexOptions.IgnoreCase);
    private static readonly Regex ExistingObject = new(" (ON|AUTHORIZATION)$", RegexOptions.IgnoreCase);
    private static readonly Regex LeadSegmentEnd = new("^([A-Za-z_]|=$)", RegexOptions.IgnoreCase);
    private static readonly Regex AfterSegmentEnd = new(@"^([A-Za-z_]|=$|\{name\}$|\{value\}$)", RegexOptions.IgnoreCase);

    private readonly KeywordProber _prober;
    private readonly string[] _pool;
    private readonly string[] _reserved;
    private readonly string[] _continuations;
    private readonly HashSet<string> _keywords;
    private readonly IReadOnlyDictionary<string, List<string>> _keywordPositions;
    private readonly IReadOnlyDictionary<string, string[]> _templates;
    private readonly Action<string?>? _progress;
    private readonly HashSet<string> _statementStarters;
    private readonly HashSet<string> _queryStarters;

    // 每個片語展開過幾層。展開到已探過的一格時，只在這次的層數比較多才再往下：OPEN 的展開先走到
    // OPEN SYMMETRIC KEY {name}，之後宣告的那一條要走得更深。
    private readonly Dictionary<string, int> _budgets = new(IgnoreCase);
    private readonly Dictionary<string, AdditivePhrase> _additiveByPosition = new(IgnoreCase);

    // 只認位置的片語探測用的文字：每個位置的第一個樣板。
    private HashSet<string> _positionPhraseProbes = new(IgnoreCase);

    // 清單片語的標頭探測文字：一項寫到這裡就開了另一份清單（BACKUP DATABASE d TO 開裝置清單），見 AddList。
    private HashSet<string> _listHeadProbes = new(IgnoreCase);

    // 固定標頭的逗號清單（CREATE LOGIN t WITH ,*）的標頭探測文字：展開走到這裡就停，見展開那一段。
    private HashSet<string> _ownedListHeadProbes = new(IgnoreCase);

    // 非清單宣告的樣式與探測文字：清單的一項是否已由宣告寫出，見 AddItemWords。
    private (string Pattern, string Probe)[] _declarations = [];

    // 括號清單與只認位置的清單一項的等號那一格，等全部宣告探完才立，見 AddItemValues。
    private readonly List<Action> _pendingItemValues = [];

    /// <param name="pool">候選字，順序就是片語裡字的順序。</param>
    /// <param name="keywordPositions">第三階段的結果：關鍵字可以出現的位置。</param>
    /// <param name="continuations">片語的續尾。</param>
    public PhraseExplorer(
        KeywordProber prober, string[] pool, string[] reserved, IEnumerable<string> keywords,
        IReadOnlyDictionary<string, List<string>> keywordPositions, IReadOnlyDictionary<string, string[]> templates,
        string[] continuations, Action<string?>? progress = null)
    {
        _prober = prober;
        _pool = pool;
        _reserved = reserved;
        _keywords = new HashSet<string>(keywords, IgnoreCase);
        _keywordPositions = keywordPositions;
        _templates = templates;
        _continuations = continuations;
        _progress = progress;

        // 語句已經完整的片語（CREATE INDEX i ON t (a) 之後）接得上的字也包括下一句的開頭；
        // 那一份在這裡探一次，從那些片語裡扣掉。
        _statementStarters = new HashSet<string>(Words("SELECT 1; "), IgnoreCase);

        // 完整的語句之後的左括號也可能是下一句：一組括號包起來的查詢（(SELECT 1)）。
        // SET CHANGE_TRACKING = ON ( 之後的 SELECT、CASE 是那一句的字，不是選項清單的。
        _queryStarters = new HashSet<string>(Words("SELECT 1; ("), IgnoreCase);
    }

    public PhraseTable Phrases { get; } = new();

    /// <summary>內建函式的名稱：函式目錄列得出，附加片語不收（見 <see cref="NameReading"/>）。</summary>
    public IReadOnlyCollection<string> BuiltInFunctions { get; init; } = new HashSet<string>(IgnoreCase);

    /// <summary>附加片語，照第一次加入的順序。</summary>
    public List<AdditivePhrase> Additive { get; } = [];

    /// <summary>物件種類之後的新名字：CREATE 之後寫到哪幾個字，下一格就是新物件的名稱。執行期拿它判斷新名字的格子。</summary>
    public List<string> CreatedKinds { get; } = [];

    /// <summary>名稱寫得出結構描述當限定字的種類（CREATE PROCEDURE dbo.p）；登入、索引、資料庫這些不行。</summary>
    public HashSet<string> SchemaQualifiedKinds { get; } = new(IgnoreCase);

    /// <summary>探測文字：代入名稱、值、括號與 Gap 之後的整段。</summary>
    /// <remarks>
    /// 值與名稱的代表寫法由剖析器挑：FETCH ABSOLUTE 之後要數字，PASSWORD = 之後要字串，兩種都不收的運算式是名稱
    /// （CONTAINSTABLE ( 的資料表與資料行）；收不了普通名稱的格子
    /// 代入那一格列得出的第一個字。等號之後一律代入列得出的值（剖析器列的或手寫的）：資料庫加密金鑰的
    /// ALGORITHM = 什麼名稱都先收，整句寫完才驗，普通名稱探得過一半、整段卻剖析不過。
    /// next 是這段之後片語的下一項：只取前一段探測時（片語裡的每一個字），名稱那一格照樣看得到它後面寫什麼。
    /// 後面還有項的 (* 代入左括號與 Items：清單有固定的第一項時（FORMAT_TYPE = …），之後的項才寫得出來。
    /// Items 只墊第一組：裡面那一組（OPENROWSET (* ORDER (*）是一項自己的括號。
    /// 清單片語（,*、以尾巴比對的 ,* ,）這裡給的是標頭，逗號之後另外探。
    /// </remarks>
    public string ProbeText(string lead, string pattern, string? group = null, string? gap = null, string? next = null, string? items = null)
    {
        // 只認位置的片語：樣板本身就是探測文字。
        if (pattern.Length == 0)
        {
            return lead;
        }

        var text = lead;
        var head = pattern.EndsWith(" ,*", StringComparison.Ordinal) ? pattern.Substring(0, pattern.Length - 3)
            : pattern.EndsWith(" ,* ,", StringComparison.Ordinal) ? pattern.Substring(0, pattern.Length - 5)
            : pattern;
        var parts = head.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        var hasRest = parts.Contains("...");

        for (var index = 0; index < parts.Length; index++)
        {
            var item = parts[index];
            var following = index + 1 < parts.Length ? parts[index + 1] : next;
            text += item switch
            {
                "{name}" => SelectName(text, following) + " ",
                "{value}" => (SelectValue(text) ?? SelectName(text, following)) + " ",
                "()" => (string.IsNullOrEmpty(group) ? "(a)" : group) + " ",
                "(*" when index < parts.Length - 1 && Array.IndexOf(parts, "(*") == index => "(" + items,
                "(*" => "(",
                "(" => "(",
                "..." => gap + " ",
                ",*" => hasRest || string.IsNullOrEmpty(gap) ? string.Empty : gap + " ",
                _ => item + " ",
            };
        }

        return text;
    }

    /// <summary>名稱的代表寫法，理由見 <see cref="ProbeText"/>。</summary>
    /// <remarks>
    /// 剖析器有的地方要看到名稱之後的字才收名稱（ALTER SERVER AUDIT a 之後非 TO、WITH 不可，接別的字在名稱就報錯）：
    /// 片語寫了下一個字面字，名稱接上它撐得過就是名稱格。
    /// 只收兩段名稱的格子（擴充事件的 package.event）一段的名稱接下一個字面字就報錯，兩段的撐得過：代入兩段。
    /// </remarks>
    public string SelectName(string probe, string? next)
    {
        // 宣告封閉的手寫值（RESTORE SERVICE 只有 MASTER）：剖析器把它當名稱讀、整句寫完才驗，
        // 代入普通名稱的話整句永遠寫不完，之後的值與字都探不出來。
        if (Phrases.WithProbe(probe).FirstOrDefault(phrase => phrase.DeclaredValues.Count > 0) is { } declared)
        {
            return declared.DeclaredValues[0];
        }

        if (!probe.EndsWith("= ", StringComparison.Ordinal) && TakesName(probe, next))
        {
            // 下一項是括號的，看名稱撐不撐得過左括號（EVENT a.b (ACTION …)）。
            var literal = next is "()" or "(*" ? "(" : next;

            if (literal != null && NameLiteral.IsMatch(literal) && !NameReaches(probe + "t", literal) && NameReaches(probe + "t.t", literal))
            {
                return "t.t";
            }

            return "t";
        }

        // 只收變數的格子（BEGIN DIALOG @h）代入變數；換成那一格列得出的字，探的就是另一種寫法（BEGIN DIALOG CONVERSATION）。
        if (TakesVariableOnly(probe))
        {
            return Continuations.PlainVariable;
        }

        var listed = Words(probe).Concat(Phrases.WithProbe(probe).SelectMany(phrase => phrase.Words));
        return listed.FirstOrDefault() ?? "t";
    }

    /// <summary>值的代表寫法：數值或字串，取剖析器在那一格收的第一種；兩種都不收的回傳 null。</summary>
    public string? SelectValue(string probe)
    {
        return _prober.FirstEndingPast(probe, Continuations.ValueSamples, probe.Length);
    }

    /// <summary>主迴圈：每條宣告在每個錨點探一次，括號清單另探逗號之後。</summary>
    public void Explore(IReadOnlyList<PhraseDeclaration> declarations)
    {
        _positionPhraseProbes = new HashSet<string>(
            declarations.Where(declaration => declaration.Pattern.Length == 0)
                .SelectMany(declaration => declaration.After ?? [])
                .Select(position => _templates[position][0]),
            IgnoreCase);
        // ... 最短是一個名稱：BACKUP DATABASE ... TO 在名稱之後就寫得出 TO，檔案清單的標頭接上 TO 就是它。
        _listHeadProbes = new HashSet<string>(
            declarations.Where(declaration => declaration.IsList || declaration.IsTailList)
                .SelectMany(declaration => Anchors(declaration).Select(anchor =>
                    ProbeText(anchor.Lead, declaration.ListHead.Replace("...", "{name}"), declaration.Group, items: declaration.Items))),
            IgnoreCase);
        _ownedListHeadProbes = new HashSet<string>(
            declarations.Where(declaration => declaration.IsList && !declaration.ListHead.Contains("..."))
                .SelectMany(declaration => Anchors(declaration).Select(anchor =>
                    ProbeText(anchor.Lead, declaration.ListHead, declaration.Group, items: declaration.Items))),
            IgnoreCase);

        // 墊了前一項的（,* NO POPULATION 的 Gap）說的是之後那一項，不算寫出了前一項。
        _declarations = declarations
            .Where(declaration => !declaration.IsList && !declaration.IsTailList && (declaration.Gap == null || declaration.Pattern.Contains("...")))
            .SelectMany(declaration => Anchors(declaration).Select(anchor =>
                (declaration.Pattern, ProbeText(anchor.Lead, declaration.Pattern, declaration.Group, declaration.Gap, items: declaration.Items))))
            .ToArray();

        foreach (var declaration in declarations)
        {
            // 墊幾種文字的 Lead 片語鍵相同，每墊一種就覆蓋一次：先記下來，最後取聯集。
            var leads = new List<ProbedPhrase>();

            foreach (var (position, lead) in Anchors(declaration))
            {
                var probe = ProbeText(lead, declaration.Pattern, declaration.Group, declaration.Gap, items: declaration.Items);

                if (declaration.IsList || declaration.IsTailList)
                {
                    var head = ProbeText(lead, declaration.ListHead, declaration.Group, declaration.Gap, items: declaration.Items);
                    AddList(declaration.Pattern, declaration.ListHead, head, position, declaration.Endings, declaration.Lagging, declaration.Values,
                        itemWords: declaration.Evidence == null);
                    continue;
                }

                Add(declaration.Pattern, probe, position, declaration.Expand, declaration.Values, declaration.Closed,
                    kinds: declaration.Kinds, extraEndings: declaration.Endings);

                var key = ProbedPhrase.Key(position, declaration.Pattern);

                if (declaration.Additive && Phrases.TryGet(key, out var additive))
                {
                    additive.Additive = true;
                    additive.Closed = false;
                }

                if (declaration.IsOpenList && Phrases.Contains(key))
                {
                    AddOpenListItems(declaration.Pattern, position, declaration.Endings);
                }

                // 只認位置的清單（CREATE INDEX … WITH ( 的 IndexOption）一項的等號之後同樣是一格。
                if (declaration.Pattern.Length == 0 && Phrases.TryGet(key, out var slot))
                {
                    var (probe0, words0, endings0) = (slot.Probe, slot.Words.ToList(), declaration.Endings);
                    _pendingItemValues.Add(() => AddItemValues(string.Empty, position, [(probe0, words0)], endings0, openList: true));
                }

                if (declaration.Lead != null && Phrases.TryGet(key, out var probed))
                {
                    leads.Add(probed);
                }
            }

            if (leads.Count > 1)
            {
                var last = leads[leads.Count - 1];
                last.Words = leads.SelectMany(phrase => phrase.Words).Distinct(IgnoreCase).ToList();
                last.Closed = leads.All(phrase => phrase.Closed);
                last.TakesVariable = leads.Any(phrase => phrase.TakesVariable);
                last.TakesName = leads.Any(phrase => phrase.TakesName);
            }
        }

        foreach (var pending in _pendingItemValues)
        {
            pending();
        }

        _pendingItemValues.Clear();
    }

    /// <summary>
    /// 片語裡的每一個字，由它前面那段列出：寫得出 CREATE OR ALTER，CREATE 之後就要有 OR、
    /// CREATE OR 之後就要有 ALTER。逐字探測問不出這種字——剖析器要看到整段才收，CREATE OR
    /// 接任何續尾都在 CREATE 就報錯——但整條片語剖析得過本身就是證據。
    /// </summary>
    /// <remarks>
    /// 前面那段已經是片語就把字補進去。還不是的另立一個，條件是那段尾巴認得出來：Lead 片語以字面字或等號結尾
    /// （以名稱或值結尾的一段，前一個字之後什麼都可能接，立了會封閉掉不相干的清單），而且至少兩項——
    /// 執行期不看 Lead 的前一格，單獨一個 ON、NEXT 到處都比對得上。單獨一個不是關鍵字的立得起來但不封閉：
    /// 它也可能是名稱，比對到只把字加進那一格的目錄。帶位置的片語從那個位置寫起，已經釘住了，
    /// 以名稱或值結尾的一段也立得起來：ALGORITHM = AES_128 之後的 ENCRYPTION 剖析器當名稱讀，只有整段是證據；
    /// 視窗框架 ROWS BETWEEN 2 之後的 PRECEDING 同理。
    /// 以 ...、括號或清單結尾的一段不立：那些元素要夾在字中間才比對得了。
    /// 帶 After 的片語，第一個字前面那段是位置本身：那個位置有只認位置的片語就補進去
    /// （函式 WITH 之後的 RETURNS、CALLED），沒有的由關鍵字目錄給。目錄也不給的（AT、ENABLE 不是
    /// 關鍵字）收進那個位置的附加片語：只加字、比對永遠是「可能」，那一格其餘的字照樣由目錄給——
    /// 立成一般的只認位置片語的話，比對確定時整份目錄讓給它，選取清單尾端只剩 AT。
    /// 附加片語只收在它比對得上的每一格都沒有別的來源列得出的字，見 <see cref="NameReading"/> 與
    /// <see cref="DropUnpositionedDuplicates"/>：多收的字在執行期不是被照目標濾掉，就是與另一份重複。
    /// </remarks>
    public void AddEvidence(IReadOnlyList<PhraseDeclaration> declarations)
    {
        // 先換掉探到的字，證據補進來的（REVOKE 之後的 GRANT）才留得住。
        AddListFirsts(declarations);

        var evidence = declarations
            .Concat(declarations.Where(declaration => declaration.Classes).SelectMany(ClassEvidence))
            .Select(declaration => (Declaration: declaration, Written: false))
            .Concat(declarations.SelectMany(WrittenEvidence).Select(declaration => (Declaration: declaration, Written: true)));

        foreach (var (declaration, written) in evidence)
        {
            var items = declaration.Pattern.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            var lead = declaration.Lead;

            if (items.Length < (lead != null ? 2 : 1))
            {
                continue;
            }

            foreach (var (position, leadText) in Anchors(declaration))
            {
                var wholeProbe = ProbeText(leadText, declaration.Pattern, declaration.Group, declaration.Gap, items: declaration.Items);

                if (!PatternAccepted(wholeProbe))
                {
                    throw new InvalidOperationException($"片語「{declaration.Pattern}」整段剖析不過（{wholeProbe}），拿它補前面那段的字沒有根據。");
                }

                AddDeclaredKind(declaration, items);

                for (var index = 0; index < items.Length; index++)
                {
                    var word = items[index];

                    if (!StartsWord.IsMatch(word))
                    {
                        continue;
                    }

                    // Lead 片語的第一個字前面那一格判不出位置。關鍵字在那裡本來就全部進場，其餘的字收進
                    // 只在判不出位置時出現的附加片語：與產生器判不出位置的關鍵字（None）同一條規則。
                    // Lead 那一段已經有片語列得出的（索引鍵之後的 INCLUDE）不必。
                    if (lead != null && index == 0)
                    {
                        if (!_keywords.Contains(word) && !Listed(leadText, word) && !NameReading(leadText, declaration, items))
                        {
                            AddAdditive("None", leadText, word);
                        }

                        continue;
                    }

                    var prefix = index == 0 ? string.Empty : string.Join(" ", items, 0, index);
                    var key = ProbedPhrase.Key(position, prefix);
                    var prefixProbe = ProbeText(leadText, prefix, declaration.Group, declaration.Gap, word, declaration.Items);

                    // Lead 片語的鍵不含 Lead：視窗框架的 ROWS 與 OFFSET 之後的 ROWS 同一個鍵，墊的文字不同就是別的片語。
                    // 帶 After 的鍵含位置，換一個樣板探（Template）仍是同一格：分析器分不開的格子，字取聯集。
                    if (lead != null && Phrases.TryGet(key, out var existing) && !IgnoreCase.Equals(existing.Probe, prefixProbe))
                    {
                        continue;
                    }

                    // 前面那段已經有片語列得出這個字（CREATE 之後的物件種類），就不必另外附加。
                    if (index == 0 && !Phrases.Contains(key))
                    {
                        var allowed = _keywordPositions.TryGetValue(word, out var positions) && positions.Contains(position, IgnoreCase);

                        if (!allowed && !Listed(prefixProbe, word) && !NameReading(leadText, declaration, items))
                        {
                            AddAdditive(position, prefixProbe, word);
                        }

                        continue;
                    }

                    // 探測文字相同就是同一格：DdlObject 的 TRIGGER {name} 與 CREATE TRIGGER {name} 都是 CREATE TRIGGER t，
                    // 字補進已經有的那一個，不另立一個互相搶比對。
                    if (!Phrases.Contains(key))
                    {
                        key = Phrases.FirstKeyWithProbe(prefixProbe) ?? key;
                    }

                    if (!Phrases.Contains(key))
                    {
                        var single = lead != null && index < 2;
                        var segmentEnd = lead != null ? LeadSegmentEnd : AfterSegmentEnd;

                        if (!segmentEnd.IsMatch(items[index - 1]) || single && _keywords.Contains(prefix))
                        {
                            continue;
                        }

                        // 手寫證據的那一格剖析器什麼都收，探出來的字不算數：只列證據的字，而且只加字。
                        if (written)
                        {
                            Phrases.Set(key, new ProbedPhrase(prefix, position, prefixProbe, []) { Additive = true });
                        }
                        else
                        {
                            Add(prefix, prefixProbe, position, closed: single ? false : null);
                        }
                    }

                    var phrase = Phrases[key];

                    if (!phrase.Words.Contains(word, IgnoreCase))
                    {
                        phrase.Words.Add(word);
                    }

                    // 中段清單探零項的證據（沒墊 Gap，或 Gap 給了 ...）寫在第一項剖析得過，字也是標頭那一格的
                    // （WITH ,* SEARCH PROPERTY LIST）；墊了項的只接在那一項之後（,* NO POPULATION）。
                    if ((declaration.Gap == null || declaration.Pattern.Contains("...")) && prefix.EndsWith(" ,*", StringComparison.Ordinal) &&
                        Phrases.TryGet(ProbedPhrase.Key(position, prefix.Substring(0, prefix.Length - 3)), out var listHead) &&
                        !listHead.Words.Contains(word, IgnoreCase))
                    {
                        listHead.Words.Add(word);
                    }
                }
            }
        }

        DropUnpositionedDuplicates();
    }

    // 手寫的證據（Evidence）：每一條接在尾巴之後，與 Classes 代入的多字類別同一種證據，不另立片語。
    private static IEnumerable<PhraseDeclaration> WrittenEvidence(PhraseDeclaration declaration) =>
        (declaration.Evidence ?? []).Select(tail => declaration with
        {
            Pattern = declaration.Pattern + " " + tail,
            Evidence = null,
            Expand = 0,
            Values = null,
            Lagging = null,
        });

    // 手寫證據的清單：標頭那一格（第一項）與逗號之後只列證據的第一個字，探到的字剖析器什麼都收、不算數；
    // 那一格也沒有名稱，封閉。
    private void AddListFirsts(IReadOnlyList<PhraseDeclaration> declarations)
    {
        foreach (var declaration in declarations.Where(declaration => declaration.IsList && declaration.Evidence != null))
        {
            var firsts = declaration.Evidence!.Select(tail => tail.Split(' ')[0]).Distinct(IgnoreCase).ToList();

            foreach (var (position, _) in Anchors(declaration))
            {
                foreach (var pattern in new[] { declaration.ListHead, declaration.Pattern })
                {
                    if (Phrases.TryGet(ProbedPhrase.Key(position, pattern), out var phrase))
                    {
                        phrase.Words = firsts.ToList();
                        phrase.Closed = true;
                    }
                }
            }
        }
    }

    private void AddCreatedKind(string kind, string probe)
    {
        if (CreatedKinds.Contains(kind, IgnoreCase))
        {
            return;
        }

        CreatedKinds.Add(kind);

        // 兩段式名稱撐過點號之後那一段：CREATE INDEX s.ix 在點號就報錯。
        var qualified = probe + Continuations.PlainName + ".";

        if (_prober.FirstRejection(qualified + Continuations.PlainName + " x") > qualified.Length)
        {
            SchemaQualifiedKinds.Add(kind);
        }
    }

    // 剖析器要看到整段才收的種類（CREATE XML SCHEMA COLLECTION、CREATE SPATIAL INDEX）CREATE 的展開探不到，
    // 宣告從 CREATE 寫到名稱的整段剖析得過，那幾個字就是建立的種類；ON、AUTHORIZATION 之後的名稱是既有的物件。
    private void AddDeclaredKind(PhraseDeclaration declaration, string[] items)
    {
        var name = Array.IndexOf(items, "{name}");

        if (declaration.Lead != null || declaration.After != null || name < 2 || !IgnoreCase.Equals(items[0], "CREATE") ||
            items.Skip(1).Take(name - 1).Any(item => !StartsWord.IsMatch(item)))
        {
            return;
        }

        var kind = string.Join(" ", items, 1, name - 1);
        var probe = ProbeText(string.Empty, "CREATE " + kind);

        if (!ExistingObject.IsMatch("CREATE " + kind) && TakesName(probe, name + 1 < items.Length ? items[name + 1] : null))
        {
            AddCreatedKind(kind, probe);
        }
    }

    // 安全性實體的類別：CREATE 展開探到的多字物件種類接在那一格之後，整段剖析得過的（ON SEARCH PROPERTY LIST）是證據。
    // 單一個字的種類由探測列；剖析不過的種類（GRANT 收不了的 EXTERNAL DATA SOURCE）不算。
    private IEnumerable<PhraseDeclaration> ClassEvidence(PhraseDeclaration declaration)
    {
        foreach (var kind in CreatedKinds.Where(kind => kind.Contains(' ')).ToList())
        {
            var pattern = declaration.Pattern.Length > 0 ? declaration.Pattern + " " + kind : kind;
            var evidence = declaration with { Pattern = pattern, Classes = false };

            if (Anchors(evidence).All(anchor => PatternAccepted(ProbeText(anchor.Lead, pattern))))
            {
                yield return evidence;
            }
        }
    }

    // 第一個字換成普通名稱整段照樣剖析得過，剖析器在那一格讀的是名稱：FROM VECTOR_SEARCH ( 與 FROM fn( 同形。
    // 名稱由函式目錄與中繼資料列，附加片語再列一次的話，照目標過濾時被濾掉，判不出位置時與函式目錄重複。
    // 普通名稱過不了而它過得了才算，與探測候選字同一條規則；SELECT a AT 的 AT 換成名稱是別名，接 TIME 就報錯。
    // 內建函式不靠這一條：引數有自己文法的（AI_GENERATE_EMBEDDINGS(x USE MODEL m)）換成名稱就剖析不過，
    // 但函式目錄照樣列它。
    private bool NameReading(string lead, PhraseDeclaration declaration, string[] items)
    {
        if (BuiltInFunctions.Contains(items[0], IgnoreCase))
        {
            return true;
        }

        var renamed = Continuations.PlainName + (items.Length > 1 ? " " + string.Join(" ", items, 1, items.Length - 1) : string.Empty);
        var renamedProbe = ProbeText(lead, renamed, declaration.Group, declaration.Gap, items: declaration.Items);

        if (!PatternAccepted(renamedProbe))
        {
            return false;
        }

        // 剖析器有的地方一項寫完才回頭驗：BEGIN x WITH ( 撐得到檔案結尾，寫完 (TRANSACTION … SNAPSHOT) 才在 x 報錯。
        // 片語自己的續尾（Endings）撐得過的，名稱讀法也要撐得過。
        var probe = ProbeText(lead, declaration.Pattern, declaration.Group, declaration.Gap, items: declaration.Items);
        return (declaration.Endings ?? []).All(ending =>
            _prober.FirstRejection(probe + ending) < (probe + ending).Length ||
            _prober.FirstRejection(renamedProbe + ending) >= (renamedProbe + ending).Length);
    }

    // None 的附加片語只在判不出位置時比對得上，那時每一個附加片語都算（只加字、取聯集）：
    // 別的位置已經收了的字（OperandTail 的 AT）在那裡一定已經列出。
    private void DropUnpositionedDuplicates()
    {
        if (!_additiveByPosition.TryGetValue("None", out var unpositioned))
        {
            return;
        }

        unpositioned.Words.RemoveAll(word => Additive.Any(other => other != unpositioned && other.Words.Contains(word, IgnoreCase)));

        if (unpositioned.Words.Count == 0)
        {
            _additiveByPosition.Remove("None");
            Additive.Remove(unpositioned);
        }
    }

    /// <summary>
    /// 語句說明登錄的名稱與別名也是證據：寫得出 DBCC CHECKDB，DBCC 之後就要有 CHECKDB。
    /// </summary>
    /// <remarks>
    /// 剖析器在那一格什麼名稱都收時（DBCC 的命令）探測問不出字，說明是唯一的名單；清單列得出的字
    /// 也就一定對得到說明。只補進已經有的片語：前面那段沒有片語的字由關鍵字目錄給（BEGIN TRY 的 TRY），
    /// 為它另立一個會把那一格其餘的字封閉掉。整段在語句開頭剖析不過的（END TRY 要在區塊裡）不算證據。
    /// </remarks>
    public void AddStatementEvidence(IEnumerable<string> names, Action<string> log)
    {
        foreach (var name in names)
        {
            var items = name.Split(' ');

            if (!PatternAccepted(ProbeText(string.Empty, name)))
            {
                log("語句說明的名稱在語句開頭剖析不過，不當證據：" + name);
                continue;
            }

            for (var index = 1; index < items.Length; index++)
            {
                if (Phrases.TryGet(ProbedPhrase.Key(StatementStart, string.Join(" ", items, 0, index)), out var phrase) &&
                    !phrase.Words.Contains(items[index], IgnoreCase))
                {
                    phrase.Words.Add(items[index]);
                }
            }
        }
    }

    /// <summary>DBCC 命令括號裡的字立成不封閉的「DBCC 命令 (*」；字從哪裡來見 <see cref="StatementDocs"/>。</summary>
    public void AddDbccArguments(IEnumerable<KeyValuePair<string, List<string>>> commands)
    {
        foreach (var command in commands.Where(command => command.Value.Count > 0))
        {
            var pattern = $"DBCC {command.Key} (*";
            Add(pattern, ProbeText(string.Empty, pattern), StatementStart, values: command.Value, closed: false);
        }
    }

    /// <summary>
    /// 選項不以逗號分隔、會重複的位置（序列選項、資料行型別之後）：一個選項寫完就回到位置本身，接得上的字併進位置片語的。
    /// 在唯一接續併項之後做，併進來的是位置片語已經併好的項。
    /// </summary>
    /// <remarks>
    /// 寫完的判法照剖析器：位置樣板接上哪一條續尾就完整（CREATE TABLE t (a int 接 <c>)</c>），以那個位置為前一格的片語接上
    /// 同一條續尾也完整，那個片語寫到這裡就是寫完一個選項（NULL、SPARSE、START WITH 1）；寫到一半的（NOT、NO）不完整，照舊。
    /// 不接名稱與值的才算，那幾格之後還有東西要寫。片語只拿一個樣板探，位置片語的字由幾個樣板的證據補齊
    /// （ALTER COLUMN 的 WITH、OPENJSON 的 AS JSON）；照自己探到的列的話，<c>ALTER COLUMN a int NOT NULL </c> 之後沒有 WITH。
    /// </remarks>
    public void ReturnCompletedOptionsToPosition()
    {
        foreach (var position in Phrases.Values.Where(phrase => phrase.Pattern.Length == 0).ToList())
        {
            var ending = Continuations.Phrases.FirstOrDefault(candidate => _prober.IsComplete(position.Probe.TrimEnd() + candidate));

            if (ending == null)
            {
                continue;
            }

            ContinueHalfOptions(position, ending);

            foreach (var phrase in Phrases.Values)
            {
                if (phrase != position && phrase.After[0] == position.After[0] && phrase.Closed && !phrase.TakesName &&
                    !phrase.TakesVariable && !phrase.TakesOperand && _prober.IsComplete(phrase.Probe.TrimEnd() + ending))
                {
                    // 唯一接續已經併過（MASKED WITH）：同一個字開頭的取位置片語那一項。
                    var firsts = new HashSet<string>(position.Words.Select(FirstWord), IgnoreCase);
                    phrase.Words = [.. position.Words, .. phrase.Words.Where(word => !firsts.Contains(FirstWord(word)))];
                }
            }
        }
    }

    /// <summary>
    /// 寫到一半的選項接上的字，單獨也是這一格另一個寫到一半的片語（<c>NO</c> 之後的 <c>MAXVALUE</c>）：
    /// 兩段接起來寫完一個選項的，立成片語，由位置片語併字。
    /// </summary>
    /// <remarks>
    /// 執行期取比對得上的最長尾巴，前一格又處處是這個位置；不立的話 <c>NO MAXVALUE </c> 比對成要值的
    /// <c>MAXVALUE</c>，寫完的一項被當成寫到一半，清單一個字都不列。單獨寫完的字（<c>NO CYCLE</c> 的 CYCLE）
    /// 比對不到片語，本來就回到位置，不必立。
    /// </remarks>
    private void ContinueHalfOptions(ProbedPhrase position, string ending)
    {
        var after = position.After[0];
        var completes = (string probe) => _prober.IsComplete(probe.TrimEnd() + ending);
        var halves = Phrases.Values
            .Where(phrase => phrase != position && phrase.After[0] == after && !completes(phrase.Probe))
            .ToList();

        foreach (var half in halves)
        {
            foreach (var word in half.Words.ToList())
            {
                var pattern = half.Pattern + " " + word;
                var probe = half.Probe + word + " ";

                if (Phrases.TryGet(ProbedPhrase.Key(after, word), out var alone) && !completes(alone.Probe) &&
                    !Phrases.Contains(ProbedPhrase.Key(after, pattern)) && completes(probe))
                {
                    Add(pattern, probe, after, child: true);
                }
            }
        }
    }

    private static string FirstWord(string item)
    {
        var space = item.IndexOf(' ');
        return space < 0 ? item : item.Substring(0, space);
    }

    /// <summary>
    /// 封閉卻一個字都沒有、名稱又收得下的格子改成不封閉：CREATE CERTIFICATE c AUTHORIZATION 之後要寫擁有者再寫 FROM，
    /// 照寫不寫得完判是封閉，而封閉的那一格什麼都不對。
    /// </summary>
    /// <remarks>
    /// 要等證據補完字才判：剖析器當名稱讀的格子（ALTER DATABASE SCOPED CONFIGURATION FOR 的 SECONDARY）探測時也是零個字，
    /// 字由整段剖析得過的片語補進來，補進來的就是那一格唯一對的字。手寫宣告封閉的不動。
    /// 收值的格子（SET ROWCOUNT 之後的數字）不是名稱格，照樣封閉。
    /// </remarks>
    public void OpenEmptyNameSlots()
    {
        foreach (var phrase in Phrases.Values.Where(phrase => phrase.Closed && phrase.TakesName && phrase.Words.Count == 0 && !phrase.DeclaredClosed))
        {
            phrase.Closed = false;
        }
    }

    /// <summary>候選字裡接得上這段探測文字的字；extra 是只有這條片語用得上的續尾。</summary>
    internal string[] Words(string probe, string[]? extra = null)
    {
        var continuations = extra is { Length: > 0 } ? [.. _continuations, .. extra] : _continuations;
        return _prober.Probe(probe, _pool, _reserved, continuations, Continuations.PlainName);
    }

    // 主迴圈與證據都照同一份錨點探：Lead 片語只有一個（判不出位置，墊 Lead），其餘是 After 的每個位置與它那個樣板。
    // 帶 After 的片語以那個位置的第一個樣板探測（Template 另外指定的除外）：它是那個位置的代表寫法，而且是完整的語句，
    // 「寫到這裡語句已經完整」的判斷才有意義。其餘樣板是第三階段為了撈齊關鍵字而加的旁支
    // （FROM t JOIN y 還缺 ON），拿來探片語只會長出那條旁支才有的字，還要多花幾倍的時間。
    private IEnumerable<(string Position, string Lead)> Anchors(PhraseDeclaration declaration)
    {
        return declaration.Lead != null
            ? [("Any", declaration.Lead), .. (declaration.AlsoLeads ?? []).Select(lead => ("Any", lead))]
            : declaration.Positions.Select(position => (position, _templates[position][declaration.Template]));
    }

    private void Add(
        string pattern, string probe, string after, int expand = 0, IReadOnlyList<string>? values = null, bool? closed = null,
        string[]? borrowed = null, bool child = false, bool step = false, ObjectKinds kinds = ObjectKinds.None, string[]? extraEndings = null)
    {
        _progress?.Invoke($"{pattern}（{after}）");
        var key = ProbedPhrase.Key(after, pattern);
        _budgets[key] = Math.Max(expand, _budgets.TryGetValue(key, out var budget) ? budget : -1);
        var endsStatement = _prober.IsComplete(probe.TrimEnd());
        IEnumerable<string> found = Words(probe, extraEndings);

        if (endsStatement)
        {
            found = found.Where(word => !_statementStarters.Contains(word));
        }
        else if (probe.EndsWith("(", StringComparison.Ordinal) && _prober.IsComplete(probe.Substring(0, probe.Length - 1).TrimEnd()))
        {
            found = found.Where(word => !_queryStarters.Contains(word));
        }

        // borrowed 是前一格寫成名稱時接得上的字：FETCH NEXT 之後的 INTO 屬於名叫 NEXT 的資料指標，
        // 不是 NEXT 帶出來的。
        if (borrowed != null)
        {
            found = found.Where(word => !borrowed.Contains(word, IgnoreCase));
        }

        var words = found.ToList();

        // 展開到的一格寫到這裡已經完整、扣掉下一句的開頭又不剩字的，這一格沒有片語可說，但展開照走：
        // FETCH ABSOLUTE 本身是名叫 ABSOLUTE 的資料指標，FETCH ABSOLUTE 1 FROM 卻是另一個讀法。
        // 值、名稱與等號那一步也一樣：之後列不出字（PASSWORD = 'x' 之後），立了只是多一條空的片語。
        var silent = (child && endsStatement || step) && words.Count == 0;

        AddValues(pattern, probe, words, values, extraEndings);

        // 名稱格：接得了名稱、接不了值。接得了值的是運算式（IS NOT DISTINCT FROM 之後），名稱只是欄位的一種寫法。
        var sample = SelectValue(probe);
        var takesName = sample == null && TakesName(probe, null);

        // 建立的物件種類寫完了，下一格是物件的名稱。ON、AUTHORIZATION 之後是既有的物件（CREATE FULLTEXT INDEX ON t、
        // CREATE SCHEMA AUTHORIZATION u），不是這一句建立的名字。
        if (kinds == ObjectKinds.New && takesName && !ExistingObject.IsMatch(pattern))
        {
            AddCreatedKind(pattern.Substring("CREATE ".Length), probe);
        }

        if (!silent)
        {
            Phrases.Set(key, new ProbedPhrase(pattern, after, probe, words)
            {
                // 物件種類之後的名稱要再寫一長段標頭才完整（CREATE SYMMETRIC KEY k WITH …），照寫不寫得完判的話
                // 名稱那一格被判成封閉；種類的片語改問名稱在那裡收不收。其餘的格子照寫不寫得完判：選項的字在剖析器眼中
                // 也是名稱（SET TRANSACTION ISOLATION 之後只有 LEVEL），只問收不收名稱的話每一格都不封閉。
                // 寫不完卻收得下名稱、最後一個字都沒有的那幾格見 OpenEmptyNameSlots。
                Closed = closed ?? (kinds != ObjectKinds.None
                    ? !takesName
                    : !_prober.AcceptsName(probe, Continuations.PlainName, _continuations)),
                // 變數寫得完一句，或這一格只收變數：BEGIN DIALOG @h 之後還要一長段標頭，續尾寫不完。
                TakesVariable = TakesVariableOnly(probe) || _prober.AcceptsName(probe, Continuations.PlainVariable, _continuations),
                TakesName = takesName,
                DeclaredClosed = closed == true,
                DeclaredValues = closed == true ? [.. (values ?? []).Where(value => !string.IsNullOrEmpty(value))] : [],
                EndsStatement = endsStatement,
                // 括號也是一個運算元：CREATE DATABASE d ON 之後是 PRIMARY 或 (，不能併成 ON PRIMARY。
                // 類別之後的 :: 也是：ALTER AUTHORIZATION ON ASSEMBLY 之後是 ::，ASSEMBLY TO 只是名叫 ASSEMBLY 的物件。
                TakesOperand = takesName || sample != null || TakesVariableOnly(probe) || _prober.FirstRejection(probe + "(") > probe.Length ||
                    _prober.FirstRejection(probe + "::") > probe.Length,
                EndsItem = _prober.FirstRejection(probe + ",") > probe.Length || _prober.FirstRejection(probe + ")") > probe.Length,
            });
        }

        // 物件種類的名稱之後只列一層（CREATE TABLE t 之後的 AS、ALTER INDEX i 之後的 ON）：
        // 一路展開的話 CREATE PROCEDURE p AS 之後就是整份語句開頭。更深的標頭由各敘述自己宣告。
        // 名稱之後那一格已由只認位置的片語說了（CREATE SEQUENCE t 之後是 SequenceOption）就不立，理由同下面的展開。
        if (kinds != ObjectKinds.None && takesName && !_positionPhraseProbes.Contains(probe + "t ") &&
            !Explored(after, pattern + " {name}", 0))
        {
            Add(pattern + " {name}", probe + "t ", after, child: true);

            // 建立的物件名稱之後接得上 AUTHORIZATION 的，擁有者那一格也是這一句的：CREATE SCHEMA s AUTHORIZATION 之後是主體。
            // 只展開名稱之後一層的話那一格沒有片語，執行期認不出它收的是既有的名稱。
            if (kinds == ObjectKinds.New &&
                Phrases.TryGet(ProbedPhrase.Key(after, pattern + " {name}"), out var named) &&
                named.Words.Contains("AUTHORIZATION", IgnoreCase) &&
                !Explored(after, pattern + " {name} AUTHORIZATION", 0))
            {
                Add(pattern + " {name} AUTHORIZATION", probe + "t AUTHORIZATION ", after, child: true);
            }
        }

        if (expand <= 0)
        {
            return;
        }

        // 值與名稱也是展開的一步：字列不出它們，它們之後的字卻只有從這條路探得到
        // （FETCH ABSOLUTE 1 之後的 FROM、DECRYPTION BY ASYMMETRIC KEY k 之後的 WITH）。
        // 不算一層，也不連著展開兩個；物件種類的名稱上面已經處理過。
        if (kinds == ObjectKinds.None && pattern.Length > 0 && !OperandEnd.IsMatch(pattern))
        {
            if (sample != null && !Explored(after, pattern + " {value}", expand))
            {
                Add(pattern + " {value}", probe + sample + " ", after, expand, child: true, step: true, extraEndings: extraEndings);
            }

            if (takesName && !Explored(after, pattern + " {name}", expand))
            {
                Add(pattern + " {name}", probe + "t ", after, expand, child: true, step: true, extraEndings: extraEndings);
            }
        }

        // 這一格寫普通名稱就完整的話（OPEN c），名稱之後接得上的字（FETCH c INTO）另探一次，展開時扣掉。
        var nameReading = _prober.IsComplete(probe + Continuations.PlainName) ? Words(probe + Continuations.PlainName + " ") : null;

        // 等號之後列得出的字是值（AES_128、RSA_2048），值之後接的與是哪一個值無關：不逐一展開，
        // 以名稱代表往下，探測代入第一個字。也收數值或字串的另外代入它往下：單位（MAXSIZE = 5 MB、UNLIMITED 之後沒有）只有這條路探得到。
        if (pattern.EndsWith(" =", StringComparison.Ordinal))
        {
            if (words.Count > 0 && !Explored(after, pattern + " {name}", expand - 1))
            {
                Add(pattern + " {name}", probe + words[0] + " ", after, expand - 1, child: true, step: true, extraEndings: extraEndings);
            }

            // 等號之後收得下一串以逗號分隔的值（PROCESS AFFINITY CPU = 0, 2 TO 3）：寫完的那幾個是中段的 ,*，
            // 否則逗號之後那個值之後的 TO 沒有片語說。
            var valuePattern = sample != null && _prober.AcceptsName(probe + sample + ", ", sample, _continuations)
                ? pattern + " ,* {value}"
                : pattern + " {value}";

            if (sample != null && !Explored(after, valuePattern, expand - 1))
            {
                Add(valuePattern, probe + sample + " ", after, expand - 1, child: true, step: true, extraEndings: extraEndings);
            }

            return;
        }

        // 手寫的值也往下：剖析器把它們當名稱看，之後的字同樣只有從這條路探得到。
        // 宣告的續尾（Endings）跟著往下：展開出來的每一格都是同一條宣告，要整段寫完才驗的字（MASKED WITH (…)）在哪一格都一樣。
        foreach (var word in words)
        {
            var childPattern = pattern.Length > 0 ? pattern + " " + word : word;
            var childProbe = probe + word + " ";

            // 已經探到這麼深的不再探；展開到的那一格已由只認位置的片語說了（觸發程序標頭的 WITH 之後是
            // TriggerOption）也不再立：同一件事說兩次。宣告了清單的標頭（CREATE SYMMETRIC KEY t WITH）同理，
            // 第一項與項的等號之後由清單立：展開先立了 WITH ALGORITHM = 的話，那一格的探測文字被它佔走，
            // 清單不立 ,* ALGORITHM =，逗號之後寫的那一項列不出值。
            if (Explored(after, childPattern, expand - 1) || _positionPhraseProbes.Contains(childProbe) ||
                _ownedListHeadProbes.Contains(childProbe))
            {
                continue;
            }

            // 語句的標頭寫完了也照樣往下探：扣掉下一句的開頭還剩字的（CREATE MASTER KEY 之後的 ENCRYPTION）
            // 是這一句的下一段。子句裡的不探：WHERE a IS NOT NULL 寫完之後接什麼由位置分析說，片語只看一個樣板，
            // 立了反而藏掉那個位置其餘的字（索引篩選之後的 WITH）。普通名稱放在同一格也完整時，完整的可能只是
            // 名稱那種讀法——OPEN SYMMETRIC 也是名叫 SYMMETRIC 的資料指標，後面照樣接 KEY——扣掉名稱讀法接得上的字。
            var completes = _prober.IsComplete(childProbe.TrimEnd());

            if (completes && !IgnoreCase.Equals(after, StatementStart) && nameReading == null)
            {
                continue;
            }

            Add(childPattern, childProbe, after, expand - 1, borrowed: completes ? nameReading : null, child: true, kinds: kinds,
                extraEndings: extraEndings);

            // 選項名稱之後的等號與字算同一層：ALGORITHM = 之後的 AES_256、RSA_2048 由剖析器列。
            // 接得了值的格子是運算式，那裡的等號是比較（WHERE CURRENT = 1），不是選項。
            if (kinds == ObjectKinds.None && sample == null &&
                _prober.FirstRejection(childProbe + "=") > childProbe.Length &&
                !Explored(after, childPattern + " =", expand - 1))
            {
                Add(childPattern + " =", childProbe + "= ", after, expand - 1, child: true, step: true, extraEndings: extraEndings);
            }
        }
    }

    // 手寫值還可以開一組清單（索引鍵之後的 WITH 只接 `(`）：清單項本身由那一格的位置片語列。
    // 宣告的續尾也算（裝置之後的 WITH 要接 STATS 這種備份選項才寫得完）。
    private void AddValues(string pattern, string probe, List<string> words, IReadOnlyList<string>? values, string[]? extraEndings)
    {
        var valueEndings = Continuations.ValueEndings.Concat((extraEndings ?? []).Where(ending => !string.IsNullOrEmpty(ending))).ToArray();

        foreach (var value in (values ?? []).Where(value => !string.IsNullOrEmpty(value)))
        {
            if (_prober.FirstEndingThrough(probe + value, valueEndings, string.Empty) == null)
            {
                throw new InvalidOperationException($"片語「{pattern}」的手寫值 {value} 剖析不過，這份清單過時了。");
            }

            if (!words.Contains(value))
            {
                words.Add(value);
            }
        }
    }

    // 清單片語（,*）與以尾巴比對的清單（,* ,）：第一項由標頭的片語給，這一條只說逗號之後。
    // 剖析器落後的選項（lagging）在探完之後補進兩格，見 PhraseDeclaration 的 Lagging；探測照舊只看剖析器收的字。
    private void AddList(
        string pattern, string headPattern, string head, string after, string[]? endings, string[]? lagging, string[]? values, bool itemWords)
    {
        var headKey = ProbedPhrase.Key(after, headPattern);

        // 宣告的 Endings 標頭也用：RESTORE … WITH MOVE 要寫完 'a' TO 'b'，第一項與逗號之後都一樣。
        if (!Phrases.Contains(headKey))
        {
            Add(headPattern, head, after, extraEndings: endings);
        }

        var firsts = Phrases[headKey].Words.ToList();

        // 一項寫到這裡就開了另一份清單的字不是這份清單的項：BACKUP DATABASE d 之後的檔案清單，TO 開的是裝置清單，
        // TO x, 之後的 DISK 屬於那一份。執行期的走訪同樣停在最近的錨點，探的話檔案清單的逗號之後會列出裝置。
        var items = ListItemWords(head, firsts.Where(first => !_listHeadProbes.Contains(head + first + " ")).ToList(), endings);

        if (items.Probe == null)
        {
            throw new InvalidOperationException($"清單片語「{pattern}」的第一項沒有一種寫得完，探不出逗號之後的字（第一項：{string.Join(", ", firsts)}）。");
        }

        AddValues(pattern, items.Probe, items.Words, values, endings);
        Phrases.Set(ProbedPhrase.Key(after, pattern), new ProbedPhrase(pattern, after, items.Probe, items.Words)
        {
            Closed = items.Closed,
            TakesVariable = items.TakesVariable,
        });

        // 以尾巴比對的清單（ALTER DATABASE d SET a ON, b）逗號之後一樣是一項：那一項寫了第一個字還沒完、項的等號之後同樣立格。
        (string, IReadOnlyList<string>)[] slots = [(head, firsts), (items.Probe, items.Words)];

        if (itemWords)
        {
            AddItemWords(headPattern + " ,*", after, slots, items.Probe.Substring(0, items.Probe.Length - 2) + " ", endings);
        }

        AddItemValues(headPattern + " ,*", after, slots, endings);

        foreach (var word in lagging ?? [])
        {
            foreach (var phrase in new[] { Phrases[headKey], Phrases[ProbedPhrase.Key(after, pattern)] })
            {
                if (!phrase.Words.Contains(word, IgnoreCase))
                {
                    phrase.Words.Add(word);
                }
            }
        }
    }

    // 逗號清單的一項寫了第一個字還沒寫完（CHANGE_TRACKING 之後是 MANUAL、AUTO、OFF）也是一格，立成中段清單的片語（標頭 ,* 項）。
    // 括號清單不必：宣告的展開立的 (* 項在左括號與逗號之後都比對得上。逗號清單的標頭只給第一項，展開又停在固定標頭的清單
    // （見展開那一段），否則逗號之後那一項的下一個字沒有片語說。接得上逗號的字已寫完一項，不立。寫完一項之後接得上的字
    // （finished 那一格：觸發程序事件之後的 AS）不算這一項的，扣掉；扣完列不出字的（DEFAULT_DATABASE 之後只有等號、
    // FOR LOGON 之後只有 AS）由 step 不立。宣告已寫出這一項的（RESULT SETS）由那一條說。第一項的那一格已有片語時
    // （標頭宣告了展開），從逗號之後那一格探，立在展開的那一條之後。
    // 手寫證據的清單（GRANT）剖析器什麼都收，探到的字不算數，不走這裡。
    private void AddItemWords(
        string listPattern, string after, (string Prefix, IReadOnlyList<string> Words)[] slots, string finished, string[]? endings)
    {
        var done = new HashSet<string>(IgnoreCase);
        var afterItem = Words(finished, endings);

        foreach (var (prefix, words) in slots)
        {
            foreach (var word in words.Where(word => StartsWord.IsMatch(word) && !word.Contains(' ') && !_keywords.Contains(word)))
            {
                var written = prefix + word;
                var slot = written + " ";
                var itemPattern = listPattern + " " + word;

                if (done.Contains(word) || _prober.FirstRejection(written + ",") > written.Length || Explored(after, itemPattern, 0))
                {
                    continue;
                }

                // 宣告寫出了這一項（RESULT SETS 從 OptionItem 寫起）：第幾項都由那一條說。同一份清單往這一項裡面寫的宣告
                // （SET ,* AUTO_CREATE_STATISTICS ON (*）不算：它說的是更後面的格子，這一項的下一個字仍由這裡立。
                if (_declarations.Any(declared => declared.Probe.StartsWith(slot, StringComparison.OrdinalIgnoreCase) &&
                    !declared.Pattern.StartsWith(itemPattern + " ", StringComparison.OrdinalIgnoreCase)))
                {
                    done.Add(word);
                    continue;
                }

                // 第一項那一格已由標頭的展開立了（ENCRYPTION BY ASYMMETRIC），改從逗號之後那一格探：同一個探測文字不立兩個片語。
                if (Phrases.WithProbe(slot).Any())
                {
                    continue;
                }

                done.Add(word);
                Add(itemPattern, slot, after, borrowed: afterItem, child: true, step: true, extraEndings: endings);
            }
        }
    }

    // 清單項的等號之後也是一格：CHECK_POLICY = 之後是 ON、OFF，PASSWORD = 'x' 之後是 HASHED、MUST_CHANGE。
    // 那一格在第幾項都一樣，立成中段清單的片語（標頭 ,* 項 =），與檔案規格的 ADD FILE ,* (* 同一種寫法；括號清單是
    // 清單某一項的開頭（WITH (* QUEUE_DELAY =），只能寫在逗號之後的項也一樣有那一格。等號之後照一般片語往下一層，
    // 值之後的尾巴由那一條探得到。等號那一格列不出字的（PASSWORD = 之後是字串）不立，與展開時的值、名稱那一步同理，
    // 往下照走。項在哪一段探測文字之後寫得出來就從那裡探：CREATE LOGIN 的第一項只能是 PASSWORD，CHECK_POLICY 要接在逗號之後。
    private void AddItemValues(
        string listPattern, string after, (string Prefix, IReadOnlyList<string> Words)[] slots, string[]? endings, bool openList = false)
    {
        var done = new HashSet<string>(IgnoreCase);

        foreach (var (prefix, words) in slots)
        {
            foreach (var word in words.Where(word => StartsWord.IsMatch(word) && !word.Contains(' ')))
            {
                var written = prefix + word;
                var slot = written + " = ";

                if (done.Contains(word) || _prober.FirstRejection(slot) <= (written + " ").Length)
                {
                    continue;
                }

                // 探測文字相同就是同一格（見 AddEvidence）：別的宣告已經立了那一格，不另立一個互相搶比對
                // （展開不走進固定標頭的清單，見展開那一段）。括號清單的一項寫在哪裡都是同一條尾巴：
                // 等號那一格沒有字、只立了值之後那一格的，以及以 Lead 宣告、哪一份清單都比對得上的那一項
                // （WITH (* DATA_COMPRESSION =、WITH (* MAX_DURATION = {value}）也算說了。
                if (Phrases.WithProbe(slot).Any() || openList && (Phrases.AnyProbeStartingWith(slot) || LeadItem(listPattern, prefix, word)))
                {
                    done.Add(word);
                    continue;
                }

                // 這一項在這份清單裡本身就不合法（FOR LOGIN 的使用者沒有 PASSWORD），剖析器在值之後不再檢查，
                // 每個字都「接得上」：與 ListItemWords 同一條防線，逗號接逗號不被拒的不算。
                var sample = SelectValue(slot);
                var value = sample ?? Words(slot).FirstOrDefault();
                var item = slot + (value ?? Continuations.PlainName) + ", ";

                if (_prober.FirstRejection(item + ",") > item.Length)
                {
                    continue;
                }

                done.Add(word);
                var itemPattern = (listPattern.Length > 0 ? listPattern + " " : string.Empty) + word + " =";

                // 收值的那一格是運算式（AI_GENERATE_CHUNKS(SOURCE = d) 寫的是資料行）：列得出的字之外名稱也寫得進去，不封閉。
                // 照整句寫不寫得完判的話，後面還有必填的項就判成封閉。
                if (!Explored(after, itemPattern, 1))
                {
                    Add(itemPattern, slot, after, expand: 1, closed: sample != null ? false : null, child: true, step: true, extraEndings: endings);
                }
            }
        }
    }

    // 以 Lead 宣告的括號清單一項（WITH (* DATA_COMPRESSION =）：執行期不看前一格，標頭的尾巴對得上的清單寫到那一項都比對得上。
    // 清單片語比尾巴（ALTER INDEX … RESUME WITH (* 以 WITH (* 結尾）；只認位置的清單比那個位置的樣板（CREATE INDEX … WITH (）。
    private bool LeadItem(string listPattern, string prefix, string word)
    {
        var item = " " + word + " =";

        foreach (var phrase in Phrases.Values.Where(phrase => phrase.After.Contains("Any")))
        {
            var at = phrase.Pattern.IndexOf("(*" + item, StringComparison.OrdinalIgnoreCase);
            var end = at + 2 + item.Length;

            if (at < 0 || end < phrase.Pattern.Length && phrase.Pattern[end] != ' ')
            {
                continue;
            }

            var head = phrase.Pattern.Substring(0, at + 2);

            if (listPattern.Length > 0
                ? listPattern.Equals(head, StringComparison.OrdinalIgnoreCase) || listPattern.EndsWith(" " + head, StringComparison.OrdinalIgnoreCase)
                : !head.Contains('{') && prefix.TrimEnd().EndsWith(head.Substring(0, head.Length - 1), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // 括號清單（(*）在左括號與逗號之後都比對得上，執行期分不出是哪一個，所以字是兩者的聯集：
    // OPENROWSET( 之後是 BULK，OPENROWSET(BULK 'x', 之後是 FORMAT、DATA_SOURCE。選項清單的兩份本來就相同。
    // 括號裡是一個子句的（WITHIN GROUP (ORDER BY a, b)）逗號屬於子句，寫成單獨的 ( 不探：聯集會讓左括號之後也列出運算式的字。
    private void AddOpenListItems(string pattern, string after, string[]? endings)
    {
        var phrase = Phrases[ProbedPhrase.Key(after, pattern)];
        var firsts = phrase.Words.ToList();
        var items = ListItemWords(phrase.Probe, firsts, endings, insideParenthesis: true);

        var probe = phrase.Probe;

        if (items.Probe == null)
        {
            _pendingItemValues.Add(() => AddItemValues(pattern, after, [(probe, firsts)], endings, openList: true));
            return;
        }

        var (itemProbe, itemWords) = (items.Probe, items.Words.ToList());
        _pendingItemValues.Add(() => AddItemValues(pattern, after, [(probe, firsts), (itemProbe, itemWords)], endings, openList: true));
        phrase.Words = [.. phrase.Words, .. items.Words.Where(word => !phrase.Words.Contains(word, IgnoreCase))];
        phrase.Closed = phrase.DeclaredClosed || phrase.Closed && items.Closed;
        phrase.TakesVariable = phrase.TakesVariable || items.TakesVariable;
    }

    // 清單裡逗號之後的字：標頭本身那個片語給第一項的字，逗號之後的字另探。第一項的寫法不只一種，
    // 用過的選項剖析器不收第二次（ALTER LOGIN l WITH NAME = n, 之後沒有 NAME），所以每一種第一項各接一個逗號探一次，
    // 取聯集；第一項受限的（CREATE LOGIN 只能先寫 PASSWORD）也因此只探那一種，之後的字不含它。
    // 逗號之後探到新字時，取第一個新字再接一個逗號往下探，直到沒有新字：順序固定的清單（VECTOR_SEARCH 的 TABLE、
    // COLUMN、SIMILAR_TO）一項只接得了下一項，只探第一項之後的話第三項以後都列不出來。順序不限的清單第二次就探不到新字；
    // 每個新字都探的話，DDL 觸發程序幾百個事件各要探一次。
    // 第一項沒有一種寫得完時 Probe 是 null。
    // 括號清單的一項要在括號裡接逗號：關上括號的續尾（INCREMENTAL = ON)）接的是括號外那一份清單的逗號，
    // 探的話 AUTO_CREATE_STATISTICS ON ( 只有一項，逗號之後卻列出整份 ALTER DATABASE SET 的選項。
    private ListItems ListItemWords(string head, IReadOnlyList<string> firsts, string[]? extraEndings, bool insideParenthesis = false)
    {
        var words = new List<string>();
        string? probe = null;
        var closed = true;
        var takesVariable = false;
        var followed = new HashSet<string>(IgnoreCase);
        var pending = new Queue<(string Prefix, string Item)>(firsts.Select(first => (head, first)));

        while (pending.Count > 0)
        {
            var (prefix, item) = pending.Dequeue();

            if (!followed.Add(item))
            {
                continue;
            }

            // 這一項寫完、接得了逗號就好，整句寫不寫得完不論：對稱金鑰的 WITH 清單之後還要寫 ENCRYPTION BY。
            // 剖析器會驗的值（FORMAT_TYPE = DELIMITEDTEXT）續尾寫不出來：等號之後代入那一格列得出的第一個字，同 ProbeText。
            // 只有那一份清單才有的寫法（VECTOR_SEARCH 的 METRIC = 'cosine'）由片語的 Endings 給。
            var endings = new List<string>(_continuations);
            endings.AddRange((extraEndings ?? []).Where(ending => !string.IsNullOrEmpty(ending)));

            if (insideParenthesis)
            {
                endings.RemoveAll(ending => ending.Count(c => c == ')') > ending.Count(c => c == '('));
            }

            var written = prefix + item;

            if (_prober.FirstRejection(written + " = ") > (written + " ").Length && Words(written + " = ").FirstOrDefault() is { Length: > 0 } value)
            {
                endings.Add(" = " + value);
            }

            var ending = _prober.FirstEndingThrough(written, endings.ToArray(), ", ");

            // 寫不完的一項（NO 之後要 CREDENTIAL）探不出逗號之後，由別的項補。
            if (ending == null)
            {
                continue;
            }

            var itemProbe = written + ending + ", ";

            // 這一項本身已經讓剖析器報了別的錯（OPENROWSET(BULK …) 不收 BATCHSIZE），之後它不再檢查：逗號接逗號也不被拒，
            // 每個保留字都「接得上」。這種探測說明不了什麼。
            if (_prober.FirstRejection(itemProbe + ",") > itemProbe.Length)
            {
                continue;
            }

            probe ??= itemProbe;
            closed = closed && !_prober.AcceptsName(itemProbe, Continuations.PlainName, _continuations);
            takesVariable = takesVariable || _prober.AcceptsName(itemProbe, Continuations.PlainVariable, _continuations);

            var listed = Words(itemProbe, extraEndings);
            var fresh = listed.Where(word => !words.Contains(word)).ToList();
            words.AddRange(fresh);

            // 一種第一項之後的逗號列得出每一種第一項（事件 FOR QN__DYNAMICS, 之後是全部事件），剖析器不記用過的項，
            // 也不分組：其餘的第一項探到的都一樣，不再探，否則幾百個事件各探一次整份候選字。
            // SET ANSI_DEFAULTS, 之後列不出 DATEFIRST（開關一組、值一組），照舊每一種各探一次。
            if (prefix == head && firsts.All(first => listed.Contains(first, IgnoreCase)))
            {
                pending = new Queue<(string Prefix, string Item)>(pending.Where(entry => entry.Prefix != head));
            }

            if (fresh.Count > 0)
            {
                pending.Enqueue((itemProbe, fresh[0]));
            }
        }

        return new ListItems(probe, closed, takesVariable, words);
    }

    // 這一格接得了名稱：普通名稱之後再接一個字，剖析器也不在名稱本身報錯。整句寫不寫得完不論——金鑰名稱之後
    // 還要寫一長段才完整，照「寫得完」判的話 CREATE SYMMETRIC KEY 之後就不是名稱。名稱寫到檔案結尾也不夠：
    // CREATE SECURITY 之後要 POLICY，剖析器要看到下一個字才在名稱報錯。
    private bool TakesName(string probe, string? next)
    {
        if (_prober.FirstRejection(probe + Continuations.PlainName + " x") > probe.Length)
        {
            return true;
        }

        // 下一項是括號的，看名稱撐不撐得過左括號（ALTER SERVICE s (ADD CONTRACT c)）。
        var literal = next is "()" or "(*" ? "(" : next;

        if (literal == null || !StartsWord.IsMatch(literal) && literal != "(")
        {
            return false;
        }

        return _prober.FirstEndingPast(probe + Continuations.PlainName + " " + literal, _continuations, probe.Length) != null;
    }

    // 這一格只收變數：變數之後再接一個字，剖析器也不在變數本身報錯，普通名稱卻過不了。只看變數的話，剖析器在更前面報
    // 別種錯的格子（DROP STATISTICS t 要兩段名稱）什麼都過得了，名稱與變數一起過的不算。
    private bool TakesVariableOnly(string probe)
    {
        return !probe.EndsWith("= ", StringComparison.Ordinal) && !TakesName(probe, null) &&
            _prober.FirstRejection(probe + Continuations.PlainVariable + " x") > probe.Length;
    }

    // 名稱寫成 text 之後撐得過下一個字面字：至少一組續尾在那個字之後才報錯。只寫到那個字為止不算，
    // 剖析器在檔案結尾才報（ADD EVENT t WITH 看不出 t 不夠）。
    private bool NameReaches(string text, string next)
    {
        var written = text + " " + next;
        return _prober.FirstEndingPast(written, _continuations, written.Length) != null;
    }

    // 撐到片語結尾就算，續尾的第一個字被拒不影響片語本身。
    private bool PatternAccepted(string probe)
    {
        return _prober.FirstEndingPast(probe, _continuations, probe.Length - 1) != null;
    }

    private bool Explored(string after, string pattern, int expand)
    {
        return _budgets.TryGetValue(ProbedPhrase.Key(after, pattern), out var budget) && budget >= expand;
    }

    // 那段探測文字已經有片語列得出這個字。
    private bool Listed(string probe, string word)
    {
        return Phrases.WithProbe(probe).Any(phrase => phrase.Words.Contains(word, IgnoreCase));
    }

    private void AddAdditive(string position, string probe, string word)
    {
        if (!_additiveByPosition.TryGetValue(position, out var additive))
        {
            _additiveByPosition.Add(position, additive = new AdditivePhrase(position));
            Additive.Add(additive);
        }

        additive.Add(word, probe);
    }

    private sealed record ListItems(string? Probe, bool Closed, bool TakesVariable, List<string> Words);
}

/// <summary>附加片語：只認位置、比對永遠是「可能」，把關鍵字目錄給不了的片語開頭加進那個位置。</summary>
public sealed class AdditivePhrase(string after)
{
    private readonly Dictionary<string, string> _probes = new(StringComparer.OrdinalIgnoreCase);

    public string After { get; } = after;

    /// <summary>第一個字補進來時的探測文字。</summary>
    /// <remarks>
    /// 跟著字走：與別的附加片語重複的字會被拿掉（<c>DropUnpositionedDuplicates</c>），留下來的探測文字要是剩下那些字的。
    /// 判不出位置的那一份借錯了的話，探測文字落在判得出的位置，附加片語在自己的探測文字上比對不到。
    /// </remarks>
    public string Probe => _probes[Words[0]];

    public List<string> Words { get; } = [];

    public void Add(string word, string probe)
    {
        if (!_probes.ContainsKey(word))
        {
            _probes.Add(word, probe);
            Words.Add(word);
        }
    }
}
