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
    private static readonly Regex AfterSegmentEnd = new(@"^([A-Za-z_]|=$|\{name\}$)", RegexOptions.IgnoreCase);

    private readonly KeywordProber _prober;
    private readonly string[] _pool;
    private readonly string[] _reserved;
    private readonly string[] _continuations;
    private readonly HashSet<string> _keywords;
    private readonly IReadOnlyDictionary<string, List<string>> _keywordPositions;
    private readonly IReadOnlyDictionary<string, string[]> _templates;
    private readonly Action<string?>? _progress;
    private readonly HashSet<string> _statementStarters;

    // 每個片語展開過幾層。展開到已探過的一格時，只在這次的層數比較多才再往下：OPEN 的展開先走到
    // OPEN SYMMETRIC KEY {name}，之後宣告的那一條要走得更深。
    private readonly Dictionary<string, int> _budgets = new(IgnoreCase);
    private readonly Dictionary<string, AdditivePhrase> _additiveByPosition = new(IgnoreCase);

    // 只認位置的片語探測用的文字：每個位置的第一個樣板。
    private HashSet<string> _positionPhraseProbes = new(IgnoreCase);

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
    }

    public PhraseTable Phrases { get; } = new();

    /// <summary>附加片語，照第一次加入的順序。</summary>
    public List<AdditivePhrase> Additive { get; } = [];

    /// <summary>物件種類之後的新名字：CREATE 之後寫到哪幾個字，下一格就是新物件的名稱。執行期拿它判斷新名字的格子。</summary>
    public List<string> CreatedKinds { get; } = [];

    /// <summary>名稱寫得出結構描述當限定字的種類（CREATE PROCEDURE dbo.p）；登入、索引、資料庫這些不行。</summary>
    public HashSet<string> SchemaQualifiedKinds { get; } = new(IgnoreCase);

    /// <summary>探測文字：代入名稱、值、括號與 Gap 之後的整段。</summary>
    /// <remarks>
    /// 值與名稱的代表寫法由剖析器挑：FETCH ABSOLUTE 之後要數字，PASSWORD = 之後要字串；收不了普通名稱的格子
    /// 代入那一格列得出的第一個字。等號之後一律代入列得出的值（剖析器列的或手寫的）：資料庫加密金鑰的
    /// ALGORITHM = 什麼名稱都先收，整句寫完才驗，普通名稱探得過一半、整段卻剖析不過。
    /// next 是這段之後片語的下一項：只取前一段探測時（片語裡的每一個字），名稱那一格照樣看得到它後面寫什麼。
    /// 後面還有項的 (* 代入左括號與 Items：清單有固定的第一項時（FORMAT_TYPE = …），之後的項才寫得出來。
    /// 清單片語（,*）這裡給的是標頭，逗號之後另外探。
    /// </remarks>
    public string ProbeText(string lead, string pattern, string? group = null, string? gap = null, string? next = null, string? items = null)
    {
        // 只認位置的片語：樣板本身就是探測文字。
        if (pattern.Length == 0)
        {
            return lead;
        }

        var text = lead;
        var parts = (pattern.EndsWith(" ,*", StringComparison.Ordinal) ? pattern.Substring(0, pattern.Length - 3) : pattern)
            .Split([' '], StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < parts.Length; index++)
        {
            var item = parts[index];
            var following = index + 1 < parts.Length ? parts[index + 1] : next;
            text += item switch
            {
                "{name}" => SelectName(text, following) + " ",
                "{value}" => (SelectValue(text) ?? "1") + " ",
                "()" => (string.IsNullOrEmpty(group) ? "(a)" : group) + " ",
                "(*" => index < parts.Length - 1 ? "(" + items : "(",
                "..." => gap + " ",
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

        foreach (var declaration in declarations)
        {
            foreach (var (position, lead) in Anchors(declaration))
            {
                var probe = ProbeText(lead, declaration.Pattern, declaration.Group, declaration.Gap, items: declaration.Items);

                if (declaration.IsList)
                {
                    AddList(declaration.Pattern, probe, position);
                    continue;
                }

                Add(declaration.Pattern, probe, position, declaration.Expand, declaration.Values, declaration.Closed,
                    kinds: declaration.Kinds, extraEndings: declaration.Endings);

                var key = ProbedPhrase.Key(position, declaration.Pattern);

                if (declaration.IsOpenList && !declaration.Clause && Phrases.Contains(key))
                {
                    AddOpenListItems(key, declaration.Endings);
                }
            }
        }
    }

    /// <summary>
    /// 片語裡的每一個字，由它前面那段列出：寫得出 CREATE OR ALTER，CREATE 之後就要有 OR、
    /// CREATE OR 之後就要有 ALTER。逐字探測問不出這種字——剖析器要看到整段才收，CREATE OR
    /// 接任何續尾都在 CREATE 就報錯——但整條片語剖析得過本身就是證據。
    /// </summary>
    /// <remarks>
    /// 前面那段已經是片語就把字補進去。還不是的另立一個，條件是那段尾巴認得出來：Lead 片語以字面字或等號結尾
    /// （以名稱或值結尾的一段，前一個字之後什麼都可能接，立了會封閉掉不相干的清單），而且至少兩項——
    /// 執行期不看 Lead 的前一格，單獨一個 ON、NEXT 到處都比對得上。單獨一個不是關鍵字的（GENERATED）立得起來但不封閉：
    /// 它也可能是名稱，比對到只把字加進那一格的目錄。帶位置的片語從那個位置寫起，已經釘住了，
    /// 以名稱結尾的一段也立得起來：ALGORITHM = AES_128 之後的 ENCRYPTION 剖析器當名稱讀，只有整段是證據。
    /// 以 ...、括號或清單結尾的一段不立：那些元素要夾在字中間才比對得了。
    /// 帶 After 的片語，第一個字前面那段是位置本身：那個位置有只認位置的片語就補進去
    /// （函式 WITH 之後的 RETURNS、CALLED），沒有的由關鍵字目錄給。目錄也不給的（AT、ENABLE 不是
    /// 關鍵字）收進那個位置的附加片語：只加字、比對永遠是「可能」，那一格其餘的字照樣由目錄給——
    /// 立成一般的只認位置片語的話，比對確定時整份目錄讓給它，選取清單尾端只剩 AT。
    /// </remarks>
    public void AddEvidence(IReadOnlyList<PhraseDeclaration> declarations)
    {
        foreach (var declaration in declarations)
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

                for (var index = 0; index < items.Length; index++)
                {
                    var word = items[index];

                    if (!StartsWord.IsMatch(word))
                    {
                        continue;
                    }

                    // Lead 片語的第一個字前面那一格判不出位置。關鍵字在那裡本來就全部進場，其餘的字（GENERATED）收進
                    // 只在判不出位置時出現的附加片語：與產生器判不出位置的關鍵字（None）同一條規則。
                    // Lead 那一段已經有片語列得出的（索引鍵之後的 INCLUDE）不必。
                    if (lead != null && index == 0)
                    {
                        if (!_keywords.Contains(word) && !Listed(leadText, word))
                        {
                            AddAdditive("None", leadText, word);
                        }

                        continue;
                    }

                    var prefix = index == 0 ? string.Empty : string.Join(" ", items, 0, index);
                    var key = ProbedPhrase.Key(position, prefix);
                    var prefixProbe = ProbeText(leadText, prefix, declaration.Group, declaration.Gap, word, declaration.Items);

                    // Lead 片語的鍵不含 Lead：視窗框架的 ROWS 與 OFFSET 之後的 ROWS 同一個鍵，墊的文字不同就是別的片語。
                    if (Phrases.TryGet(key, out var existing) && !IgnoreCase.Equals(existing.Probe, prefixProbe))
                    {
                        continue;
                    }

                    // 前面那段已經有片語列得出這個字（CREATE 之後的物件種類），就不必另外附加。
                    if (index == 0 && !Phrases.Contains(key))
                    {
                        var allowed = _keywordPositions.TryGetValue(word, out var positions) && positions.Contains(position, IgnoreCase);

                        if (!allowed && !Listed(prefixProbe, word))
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

                        Add(prefix, prefixProbe, position, closed: single ? false : null);
                    }

                    var phrase = Phrases[key];

                    if (!phrase.Words.Contains(word, IgnoreCase))
                    {
                        phrase.Words.Add(word);
                    }
                }
            }
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
            ? [("Any", declaration.Lead)]
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

        // 手寫值還可以開一組清單（索引鍵之後的 WITH 只接 `(`）：清單項本身由那一格的位置片語列。
        foreach (var value in (values ?? []).Where(value => !string.IsNullOrEmpty(value)))
        {
            if (_prober.FirstEndingThrough(probe + value, Continuations.ValueEndings, string.Empty) == null)
            {
                throw new InvalidOperationException($"片語「{pattern}」的手寫值 {value} 剖析不過，這份清單過時了。");
            }

            if (!words.Contains(value))
            {
                words.Add(value);
            }
        }

        // 名稱格：接得了名稱、接不了值。接得了值的是運算式（IS NOT DISTINCT FROM 之後），名稱只是欄位的一種寫法。
        var sample = SelectValue(probe);
        var takesName = sample == null && TakesName(probe, null);

        // 建立的物件種類寫完了，下一格是物件的名稱。ON、AUTHORIZATION 之後是既有的物件（CREATE FULLTEXT INDEX ON t、
        // CREATE SCHEMA AUTHORIZATION u），不是這一句建立的名字。
        if (kinds == ObjectKinds.New && takesName && !ExistingObject.IsMatch(pattern))
        {
            var kind = pattern.Substring("CREATE ".Length);
            CreatedKinds.Add(kind);

            // 兩段式名稱撐過點號之後那一段：CREATE INDEX s.ix 在點號就報錯。
            var qualified = probe + Continuations.PlainName + ".";

            if (_prober.FirstRejection(qualified + Continuations.PlainName + " x") > qualified.Length)
            {
                SchemaQualifiedKinds.Add(kind);
            }
        }

        if (!silent)
        {
            Phrases.Set(key, new ProbedPhrase(pattern, after, probe, words)
            {
                // 物件種類之後的名稱要再寫一長段標頭才完整（CREATE SYMMETRIC KEY k WITH …），照寫不寫得完判的話
                // 名稱那一格被判成封閉；種類的片語改問名稱在那裡收不收。
                Closed = closed ?? (kinds != ObjectKinds.None
                    ? !takesName
                    : !_prober.AcceptsName(probe, Continuations.PlainName, _continuations)),
                TakesVariable = _prober.AcceptsName(probe, Continuations.PlainVariable, _continuations),
                EndsStatement = endsStatement,
                TakesOperand = takesName || sample != null,
            });
        }

        // 物件種類的名稱之後只列一層（CREATE TABLE t 之後的 AS、ALTER INDEX i 之後的 ON）：
        // 一路展開的話 CREATE PROCEDURE p AS 之後就是整份語句開頭。更深的標頭由各敘述自己宣告。
        // 名稱之後那一格已由只認位置的片語說了（CREATE SEQUENCE t 之後是 SequenceOption）就不立，理由同下面的展開。
        if (kinds != ObjectKinds.None && takesName && !_positionPhraseProbes.Contains(probe + "t ") &&
            !Explored(after, pattern + " {name}", 0))
        {
            Add(pattern + " {name}", probe + "t ", after, child: true);
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
                Add(pattern + " {value}", probe + sample + " ", after, expand, child: true, step: true);
            }

            if (takesName && !Explored(after, pattern + " {name}", expand))
            {
                Add(pattern + " {name}", probe + "t ", after, expand, child: true, step: true);
            }
        }

        // 這一格寫普通名稱就完整的話（OPEN c），名稱之後接得上的字（FETCH c INTO）另探一次，展開時扣掉。
        var nameReading = _prober.IsComplete(probe + Continuations.PlainName) ? Words(probe + Continuations.PlainName + " ") : null;

        // 等號之後列得出的字是值（AES_128、RSA_2048），值之後接的與是哪一個值無關：不逐一展開，
        // 以名稱代表往下，探測代入第一個字。
        if (pattern.EndsWith(" =", StringComparison.Ordinal))
        {
            if (words.Count > 0 && !Explored(after, pattern + " {name}", expand - 1))
            {
                Add(pattern + " {name}", probe + words[0] + " ", after, expand - 1, child: true, step: true);
            }

            return;
        }

        // 手寫的值也往下：剖析器把它們當名稱看，之後的字同樣只有從這條路探得到。
        foreach (var word in words)
        {
            var childPattern = pattern.Length > 0 ? pattern + " " + word : word;
            var childProbe = probe + word + " ";

            // 已經探到這麼深的不再探；展開到的那一格已由只認位置的片語說了（觸發程序標頭的 WITH 之後是
            // TriggerOption）也不再立：同一件事說兩次。
            if (Explored(after, childPattern, expand - 1) || _positionPhraseProbes.Contains(childProbe))
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

            Add(childPattern, childProbe, after, expand - 1, borrowed: completes ? nameReading : null, child: true, kinds: kinds);

            // 選項名稱之後的等號與字算同一層：ALGORITHM = 之後的 AES_256、RSA_2048 由剖析器列。
            // 接得了值的格子是運算式，那裡的等號是比較（WHERE CURRENT = 1），不是選項。
            if (kinds == ObjectKinds.None && sample == null &&
                _prober.FirstRejection(childProbe + "=") > childProbe.Length &&
                !Explored(after, childPattern + " =", expand - 1))
            {
                Add(childPattern + " =", childProbe + "= ", after, expand - 1, child: true, step: true);
            }
        }
    }

    // 清單片語（,*）：第一項由標頭的片語給，這一條只說逗號之後。
    private void AddList(string pattern, string head, string after)
    {
        var headPattern = pattern.Substring(0, pattern.Length - " ,*".Length);
        var headKey = ProbedPhrase.Key(after, headPattern);

        if (!Phrases.Contains(headKey))
        {
            Add(headPattern, head, after);
        }

        var firsts = Phrases[headKey].Words.ToList();
        var items = ListItemWords(head, firsts, null);

        if (items.Probe == null)
        {
            throw new InvalidOperationException($"清單片語「{pattern}」的第一項沒有一種寫得完，探不出逗號之後的字（第一項：{string.Join(", ", firsts)}）。");
        }

        Phrases.Set(ProbedPhrase.Key(after, pattern), new ProbedPhrase(pattern, after, items.Probe, items.Words)
        {
            Closed = items.Closed,
            TakesVariable = items.TakesVariable,
        });
    }

    // 括號清單（(*）在左括號與逗號之後都比對得上，執行期分不出是哪一個，所以字是兩者的聯集：
    // OPENROWSET( 之後是 BULK，OPENROWSET(BULK 'x', 之後是 FORMAT、DATA_SOURCE。選項清單的兩份本來就相同。
    // 括號裡是一個子句的（WITHIN GROUP (ORDER BY a, b)）逗號屬於子句，由 Clause 宣告不探：聯集會讓左括號之後也列出運算式的字。
    private void AddOpenListItems(string key, string[]? endings)
    {
        var phrase = Phrases[key];
        var items = ListItemWords(phrase.Probe, phrase.Words.ToList(), endings);

        if (items.Probe == null)
        {
            return;
        }

        phrase.Words = [.. phrase.Words, .. items.Words.Where(word => !phrase.Words.Contains(word, IgnoreCase))];
        phrase.Closed = phrase.Closed && items.Closed;
        phrase.TakesVariable = phrase.TakesVariable || items.TakesVariable;
    }

    // 清單裡逗號之後的字：標頭本身那個片語給第一項的字，逗號之後的字另探。第一項的寫法不只一種，
    // 用過的選項剖析器不收第二次（ALTER LOGIN l WITH NAME = n, 之後沒有 NAME），所以每一種第一項各接一個逗號探一次，
    // 取聯集；第一項受限的（CREATE LOGIN 只能先寫 PASSWORD）也因此只探那一種，之後的字不含它。
    // 逗號之後探到新字時，取第一個新字再接一個逗號往下探，直到沒有新字：順序固定的清單（VECTOR_SEARCH 的 TABLE、
    // COLUMN、SIMILAR_TO）一項只接得了下一項，只探第一項之後的話第三項以後都列不出來。順序不限的清單第二次就探不到新字；
    // 每個新字都探的話，DDL 觸發程序幾百個事件各要探一次。
    // 第一項沒有一種寫得完時 Probe 是 null。
    private ListItems ListItemWords(string head, IReadOnlyList<string> firsts, string[]? extraEndings)
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

            var fresh = Words(itemProbe).Where(word => !words.Contains(word)).ToList();
            words.AddRange(fresh);

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

        if (next == null || !StartsWord.IsMatch(next))
        {
            return false;
        }

        return _prober.FirstEndingPast(probe + Continuations.PlainName + " " + next, _continuations, probe.Length) != null;
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
            _additiveByPosition.Add(position, additive = new AdditivePhrase(position, probe));
            Additive.Add(additive);
        }

        if (!additive.Words.Contains(word))
        {
            additive.Words.Add(word);
        }
    }

    private sealed record ListItems(string? Probe, bool Closed, bool TakesVariable, List<string> Words);
}

/// <summary>附加片語：只認位置、比對永遠是「可能」，把關鍵字目錄給不了的片語開頭加進那個位置。</summary>
public sealed class AdditivePhrase(string after, string probe)
{
    public string After { get; } = after;

    public string Probe { get; } = probe;

    public List<string> Words { get; } = [];
}
