using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlAssist.KeywordGenerator;

/// <summary>
/// tools/Generate-Keywords.ps1 的探測器：每一個判定都是「剖析器收不收這段文字」，腳本只交代問什麼。
/// 剖析事實記進快取（<see cref="ProbeFacts"/>），解讀的規則在這裡，每次重算。
/// </summary>
public sealed class KeywordProber
{
    // SQL Server 2025（相容層級 170）的剖析器，舊版 ScriptDom 退到前幾版。ScriptDom 已經帶 TSql180Parser，
    // 但換它會改到產物。
    private static readonly string[] ParserNames = { "TSql170Parser", "TSql160Parser", "TSql150Parser" };

    // 撐過字本身（拒收落在續尾或之後）、整段都過、回頭在前面拒收：每個位元組四個字一次查。
    private static readonly byte[] PastWord = ClassMask(value => value >= ProbeFacts.InContinuation);
    private static readonly byte[] WholeAccepted = ClassMask(value => value == ProbeFacts.Whole);
    private static readonly byte[] Recanted = ClassMask(value => value == ProbeFacts.Recanted);

    private readonly ProbeFacts _facts;
    private readonly ProbeCache _cache;
    private readonly ConcurrentDictionary<string, Lazy<Ending>> _endings =
        new ConcurrentDictionary<string, Lazy<Ending>>(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string[]> _probes = new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<string[], PoolWords> _pools = new ConditionalWeakTable<string[], PoolWords>();

    /// <param name="rejecting">算拒收的錯誤碼；其餘錯誤（46029 未預期的檔案結尾）代表吃下去了、只是沒寫完。</param>
    /// <param name="cachePath">剖析事實的快取檔。</param>
    /// <param name="loadCache">false 時不讀舊快取，結果照樣寫回。</param>
    public KeywordProber(int[] rejecting, string cachePath, bool loadCache)
    {
        var assembly = typeof(TSqlParser).Assembly;
        var parserType = ParserNames
            .Select(name => assembly.GetType("Microsoft.SqlServer.TransactSql.ScriptDom." + name))
            .FirstOrDefault(type => type != null)
            ?? throw new InvalidOperationException("ScriptDom 裡找不到可用的 TSqlNNNParser 型別。");

        ScriptDomPath = assembly.Location;
        ScriptDomVersion = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
        ParserName = parserType.Name;
        _facts = new ProbeFacts(parserType, rejecting);
        _cache = new ProbeCache(
            string.Join("|", ScriptDomVersion, ParserName, string.Join(",", rejecting), ProbeFacts.SourceHash()), cachePath);

        if (loadCache)
        {
            _cache.Load();
        }
    }

    private enum Ending
    {
        None,
        Item,
        Statement,
    }

    /// <summary>實際載入的 ScriptDom；腳本拿它確認用的是 SSMS 那一份。</summary>
    public string ScriptDomPath { get; }

    public string ScriptDomVersion { get; }

    public string ParserName { get; }

    public string CachePath => _cache.Path;

    /// <summary>第一階段：TSqlTokenType 的成員名稱大寫後丟回 tokenizer，對得回原成員的才是字面值。</summary>
    /// <remarks>
    /// 標點與字面值（Comma、HexLiteral…）自然對不回來。名稱含 camelCase 轉折的再試補底線的寫法，
    /// 撈回 CURRENT_TIMESTAMP、IDENTITY_INSERT 這一類。順序同列舉。
    /// </remarks>
    public string[] ReadLiterals()
    {
        var names = Enum.GetNames(typeof(TSqlTokenType));
        var literals = new string?[names.Length];

        Parallel.For(0, names.Length, index =>
        {
            var name = names[index];
            var upper = name.ToUpperInvariant();

            if (RoundTrips(upper, name))
            {
                literals[index] = upper;
                return;
            }

            // CurrentTimestamp → CURRENT_TIMESTAMP
            var underscored = Regex.Replace(name, "(?<!^)([A-Z])", "_$1").ToUpperInvariant();

            if (underscored != upper && RoundTrips(underscored, name))
            {
                literals[index] = underscored;
            }
        });

        return literals.OfType<string>().ToArray();
    }

    /// <summary>第二階段：塞進識別字的洞裡會被拒的字，插入時一定要加方括號。順序同 names。</summary>
    /// <remarks>
    /// 洞在樣板中間，所以樣板是前後綴成對。樣板本身是完整語句，名字之前（含名字）出現任何錯誤都只可能是它造成的。
    /// </remarks>
    public string[] RejectedAsIdentifiers(string[] names, string[] prefixes, string[] suffixes)
    {
        var rejected = new bool[names.Length];

        Parallel.For(0, names.Length, index =>
        {
            for (var template = 0; template < prefixes.Length && !rejected[index]; template++)
            {
                var limit = prefixes[template].Length + names[index].Length;
                _facts.Parse(prefixes[template] + names[index] + suffixes[template], out var errors);
                rejected[index] = errors.Any(error => error.Offset <= limit);
            }
        });

        return names.Where((_, index) => rejected[index]).ToArray();
    }

    /// <summary>第三階段：每個關鍵字在每個位置是否合法；位置的樣板任一個接得上就算。</summary>
    public bool[][] ClassifyPositions(string[] keywords, bool[] canBeName, string[][] templates, string[] continuations, string plain)
    {
        var result = new bool[keywords.Length][];

        Parallel.For(0, keywords.Length, index =>
        {
            result[index] = templates
                .Select(group => group.Any(prefix => KeywordAllowed(prefix, keywords[index], canBeName[index], continuations, plain)))
                .ToArray();
        });

        EndBatch();
        return result;
    }

    /// <summary>子句片語：候選字裡接得上這段探測文字的字，順序同 pool。</summary>
    /// <remarks>腳本對同一格會問好幾次（名稱格的代表寫法、展開時的扣除），整組問題記一份答案。</remarks>
    public string[] Probe(string probe, string[] pool, string[] reserved, string[] continuations, string plain)
    {
        var words = WordsOf(pool);
        var question = string.Join("\u0001", words.KeyWith(plain), WordsOf(reserved).Key, probe, string.Join("\u0002", continuations));
        return (string[])_probes.GetOrAdd(question, _ => ComputeProbe(probe, words, reserved, continuations, plain)).Clone();
    }

    /// <summary>普通名稱配上任何一條續尾組得成完整的語句，這一格就不封閉。</summary>
    /// <remarks>
    /// 要完整而不只是沒被拒：SET TRANSACTION Lib_Reader 在檔案結尾之前一個錯都沒有，
    /// 剖析器要看到後面的 LEVEL 才說「必須是 ISOLATION」。
    /// </remarks>
    public bool AcceptsName(string probe, string plain, string[] continuations)
    {
        return continuations.Any(continuation => Complete(probe, plain, continuation));
    }

    /// <summary>最早一個拒收錯誤的位置；沒有就是 int.MaxValue。</summary>
    public int FirstRejection(string text)
    {
        return _cache.Rejection.Get(text, _ => _facts.FirstRejection(text));
    }

    public bool IsComplete(string text)
    {
        return Complete(text, string.Empty, string.Empty);
    }

    /// <summary>text 接上哪一條續尾，最早的拒收落在 limit 之後；照 endings 的順序取第一條，都不行是 null。</summary>
    public string? FirstEndingPast(string text, string[] endings, int limit)
    {
        return endings.FirstOrDefault(ending => FirstRejection(text + ending) > limit);
    }

    /// <summary>prefix 接上哪一條續尾再接 suffix，整條續尾撐得過；照 endings 的順序取第一條，都不行是 null。</summary>
    public string? FirstEndingThrough(string prefix, string[] endings, string suffix)
    {
        return endings.FirstOrDefault(ending => FirstRejection(prefix + ending + suffix) > prefix.Length + ending.Length);
    }

    /// <summary>第五階段：本身就把前一格開的那一項寫完的字（NULL、DESC），順序同 keywords。</summary>
    /// <remarks>
    /// 任一個樣板接上它就是完整的一句，而以它結尾的是語句裡的一項；非保留字照第三階段的規則，
    /// 普通名稱接上去也成立的不算。
    /// </remarks>
    public string[] ItemEndings(string[] keywords, bool[] canBeName, string[][] templates, string plain)
    {
        var ends = new bool[keywords.Length];

        Parallel.For(0, keywords.Length, index =>
        {
            ends[index] = templates.SelectMany(group => group).Any(template =>
                EndingOf(template, keywords[index]) == Ending.Item &&
                !(canBeName[index] && EndingOf(template, plain) == Ending.Item));
        });

        return keywords.Where((_, index) => ends[index]).ToArray();
    }

    /// <summary>第六階段：寫完一整句的字，以及前一格在哪些位置時寫完那一句就只剩下一句可接。</summary>
    /// <remarks>
    /// 收得乾淨是指那一句再也接不了語句開頭以外的東西：COMMIT 還接 TRAN、RETURN 還接運算式、
    /// BEGIN TRAN 還接交易名稱的變數，把它們判成語句開頭就把這些字藏起來了。
    /// 每個位置以第一個接得上的樣板為準；非保留字照第三階段的規則，普通名稱也寫得完一句的不算。
    /// </remarks>
    public StatementEnding[] StatementEndings(
        string[] keywords, bool[] canBeName, string[] positionNames, string[][] templates, bool[][] allowed,
        string[] reserved, string[] continuations, string plain)
    {
        var firsts = new string?[keywords.Length][];

        Parallel.For(0, keywords.Length, index =>
        {
            firsts[index] = templates
                .Select(group => group.FirstOrDefault(template =>
                    EndingOf(template, keywords[index]) == Ending.Statement &&
                    !(canBeName[index] && EndingOf(template, plain) == Ending.Statement)))
                .ToArray();
        });

        var statementStart = Array.IndexOf(positionNames, "StatementStart");
        var starters = new HashSet<string>(keywords.Where((_, index) => allowed[index][statementStart]), StringComparer.Ordinal);
        var result = new List<StatementEnding>();

        // 探測各自平行，彼此之間不平行：檢查點趁批次之間存，那時沒有工作在寫快取。
        for (var index = 0; index < keywords.Length; index++)
        {
            var closes = new List<string>();

            for (var position = 0; position < positionNames.Length; position++)
            {
                var template = firsts[index][position];

                if (template == null)
                {
                    continue;
                }

                var probe = template + keywords[index] + " ";
                var hidden = Probe(probe, keywords, reserved, continuations, plain).Any(word => !starters.Contains(word));
                var takesVariable = FirstRejection(probe + "@v") > probe.Length + 2;

                if (!hidden && !takesVariable)
                {
                    closes.Add(positionNames[position]);
                }
            }

            if (firsts[index].Any(template => template != null))
            {
                result.Add(new StatementEnding(keywords[index], closes.ToArray()));
            }
        }

        return result.ToArray();
    }

    /// <summary>寫回快取；拼接比對不上過的話整份刪掉。prune 見 <see cref="ProbeCache.Save"/>。</summary>
    public void SaveCache(bool prune)
    {
        if (_facts.Mismatched)
        {
            _cache.Delete();
            return;
        }

        _cache.Save(prune);
    }

    public string CacheSummary()
    {
        return string.Format("剖析 {0} 次；快取沿用 {1} 筆、新增 {2} 筆", _facts.Parses, _cache.Hits, _cache.Misses);
    }

    private PoolWords WordsOf(string[] words)
    {
        return _pools.GetValue(words, key => new PoolWords(key));
    }

    private string[] ComputeProbe(string probe, PoolWords pool, string[] reserved, string[] continuations, string plain)
    {
        var reservedSet = new HashSet<string>(reserved, StringComparer.OrdinalIgnoreCase);
        var count = pool.Words.Length;

        // 普通名稱排在最後一格，與候選字一起分類。
        var words = new string[count + 1];
        Array.Copy(pool.Words, words, count);
        words[count] = plain;
        var wordsKey = pool.KeyWith(plain);

        // 每個位元組裝四個字的分類；判定也以位元組為單位查表，四個字一次算完。
        var length = (count + 4) / 4;
        var reservedBits = new byte[length];
        var passed = new byte[length];
        var recanted = new byte[length];

        for (var index = 0; index < count; index++)
        {
            if (reservedSet.Contains(pool.Words[index]))
            {
                reservedBits[index / 4] |= (byte)(1 << (index % 4));
            }
        }

        foreach (var continuation in continuations)
        {
            var classes = _cache.Classes.Get(wordsKey + "\u0001" + probe + "\u0001" + continuation,
                key => _facts.Classes(probe, words, continuation));
            var plainClass = ClassOf(classes, count);

            // 保留字當不了名字，被接受就一定是以關鍵字的身分。普通名稱在字本身就被拒、而候選字撐過了字本身，
            // 也算：ROWS BETWEEN UNBOUNDED 後面要接 PRECEDING 才完整，整段比對的話它與普通名稱一起被拒。
            var asName = plainClass <= ProbeFacts.InWord ? PastWord : plainClass <= ProbeFacts.InContinuation ? WholeAccepted : null;

            for (var index = 0; index < length; index++)
            {
                var packed = classes[index];

                // 剖析器有的地方先收下、讀完才回頭驗：DECRYPTION BY CERTIFICATE KEY 到檔案結尾都沒被拒，
                // 接上金鑰名稱才在 CERTIFICATE 報錯。回頭拒收過的字，只有整句寫得完才算接得上。
                recanted[index] |= Recanted[packed];
                passed[index] |= (byte)((reservedBits[index] & PastWord[packed]) | (~reservedBits[index] & (asName?[packed] ?? 0)));
            }
        }

        var accepted = new bool[count];

        Parallel.For(0, count, index =>
        {
            accepted[index] = IsSet(passed, index) &&
                (!IsSet(recanted, index) || continuations.Any(continuation => Complete(probe, pool.Words[index], continuation)));
        });

        EndBatch();
        return pool.Words.Where((_, index) => accepted[index]).ToArray();
    }

    private bool RoundTrips(string text, string tokenType)
    {
        var tokens = _facts.Tokenize(text);
        return tokens.Count >= 1 && tokens[0].TokenType.ToString() == tokenType;
    }

    private bool Complete(string prefix, string word, string continuation)
    {
        return _cache.Complete.Get(prefix + word + continuation, _ => _facts.IsComplete(prefix, word, continuation));
    }

    // 非保留字（APPLY、NOLOCK、GO…）當名字寫也合法，所以任何接受名稱的位置都「接受」它們：
    // CREATE TABLE t ( 之後的 NOLOCK 只是一個叫 NOLOCK 的資料行。一條規則分開兩種情形，
    // 不分位置：同一組續尾換成普通名稱也過的話，那一次只證明它能當名字，不算數；
    // 普通名稱過不了而它過得了，才是它以關鍵字的身分屬於這個位置（BEGIN TRY）。
    // 這一比看的是整段而不只到字為止：SELECT Lib_Reader VALUE FOR s 在名稱之後才出錯，
    // 只看到名稱為止的話它也「過」，NEXT VALUE FOR 就分不出來。
    // 保留字不必比：它們當不了名字，被接受就一定是以關鍵字的身分。
    private bool KeywordAllowed(string prefix, string keyword, bool canBeName, string[] continuations, string plain)
    {
        return continuations.Any(continuation => canBeName
            ? Accepted(prefix, keyword, continuation, true) && !Accepted(prefix, plain, continuation, true)
            : Accepted(prefix, keyword, continuation, false));
    }

    // whole：整段都要過，不只到這個字為止。只到字為止的判定看不出 46097 跳過的是哪裡，不看它。
    private bool Accepted(string prefix, string word, string continuation, bool whole)
    {
        var facts = _cache.Accepted.Get(prefix + word + continuation, _ => _facts.AcceptedFacts(prefix, word, continuation));

        if (whole && (facts >> 32) != 0)
        {
            return false;
        }

        var limit = prefix.Length + word.Length + (whole ? continuation.Length : 0);
        return (int)(facts & 0xFFFFFFFFL) > limit;
    }

    /// <summary>樣板接上這個字之後，這個字寫完的是語句裡的一項、語句本身，還是都不是（不完整也算不是）。</summary>
    /// <remarks>
    /// 一項是語法樹裡語句以外的片段。BEGIN TRAN 也完整，但以 TRAN 結尾的只有語句本身——那種字之後往回找子句，
    /// 找到的是上一句的；SELECT a COMMIT 的 COMMIT 是下一句，批次分隔的 GO 不在任何片段裡，同樣不算。
    /// 第五、六階段問同一組樣板，普通名稱又每個關鍵字都要比一次：每一組只剖析一次。
    /// </remarks>
    private Ending EndingOf(string template, string word)
    {
        return _endings.GetOrAdd(template + "\u0001" + word, _ => new Lazy<Ending>(() =>
        {
            var text = template + word;
            var fragment = _facts.Parse(text, out var errors);

            if (errors.Count > 0 || fragment == null)
            {
                return Ending.None;
            }

            var items = new ItemEndingFinder(template.Length, text.Length);
            fragment.Accept(items);

            if (items.Found)
            {
                return Ending.Item;
            }

            var statements = new StatementFinder(template.Length);
            fragment.Accept(statements);
            var innermost = statements.Innermost;
            return innermost != null && innermost.StartOffset + innermost.FragmentLength == text.Length ? Ending.Statement : Ending.None;
        })).Value;
    }

    // 前綴連同字的詞元只在同一批探測裡重複用到；檢查點也趁批次之間存，那時沒有平行工作在寫快取。
    private void EndBatch()
    {
        _facts.ForgetHeads();

        if (_cache.CheckpointDue)
        {
            SaveCache(false);
        }
    }

    private static byte ClassOf(byte[] packed, int index)
    {
        return (byte)((packed[index / 4] >> (index % 4 * 2)) & 3);
    }

    private static bool IsSet(byte[] bits, int index)
    {
        return (bits[index / 4] & (1 << (index % 4))) != 0;
    }

    // 一個位元組四個字的分類 → 四個字各自合不合條件，一個字一個位元。
    private static byte[] ClassMask(Func<int, bool> test)
    {
        var mask = new byte[256];

        for (var packed = 0; packed < 256; packed++)
        {
            for (var slot = 0; slot < 4; slot++)
            {
                if (test((packed >> (slot * 2)) & 3))
                {
                    mask[packed] |= (byte)(1 << slot);
                }
            }
        }

        return mask;
    }

    /// <summary>候選字清單與它的雜湊：腳本每次傳的是同一個陣列，雜湊只算一次。</summary>
    private sealed class PoolWords
    {
        public PoolWords(string[] words)
        {
            Words = words;

            _hash = Hash(14695981039346656037UL, words);
        }

        public string[] Words { get; }

        public string Key => _hash.ToString("x16");

        /// <summary>候選字後面再接一個字的雜湊，等於把它併進清單再算。</summary>
        public string KeyWith(string word)
        {
            return Hash(_hash, new[] { word }).ToString("x16");
        }

        // FNV-1a 64 位元：候選字清單只隨 ScriptDom 或補充清單改變，拿來區分快取項夠了。
        private readonly ulong _hash;

        private static ulong Hash(ulong hash, string[] words)
        {
            foreach (var word in words)
            {
                foreach (var c in word + "\n")
                {
                    hash = (hash ^ c) * 1099511628211UL;
                }
            }

            return hash;
        }
    }
}

/// <summary>寫完一整句的字，以及前一格在哪些位置時寫完那一句就只剩下一句可接。</summary>
public sealed class StatementEnding
{
    public StatementEnding(string word, string[] closes)
    {
        Word = word;
        Closes = closes;
    }

    public string Word { get; }

    public string[] Closes { get; }
}
