using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlAssist.KeywordGenerator;

/// <summary>剖析器說了什麼：存進快取的事實由這裡算出，怎麼解讀在 <see cref="KeywordProber"/>。</summary>
internal sealed class ProbeFacts
{
    private readonly ThreadLocal<TSqlParser> _parser;
    private readonly HashSet<int> _rejecting;
    private long _parses;

    public ProbeFacts(Type parserType, IEnumerable<int> rejecting)
    {
        // 建構參數是 initialQuotedIdentifiers；只傳 true 會落到 nonPublic 那個多載。
        _parser = new ThreadLocal<TSqlParser>(() => (TSqlParser)Activator.CreateInstance(parserType, new object[] { true }));
        _rejecting = new HashSet<int>(rejecting);
    }

    public long Parses => Interlocked.Read(ref _parses);

    /// <summary>拼接與整段剖析對不上過；之前沒抽到的那些也可能是錯的。</summary>
    public bool Mismatched => _mismatch;

    /// <summary>
    /// 下面那個區段的雜湊。區段算出的是存進快取的事實，文字一變快取就整份作廢；
    /// 原始檔嵌在組件裡，雜湊的一定是載入的這一份程式。
    /// </summary>
    public static string SourceHash()
    {
        using var stream = typeof(ProbeFacts).Assembly.GetManifestResourceStream("ProbeFacts.cs")
            ?? throw new InvalidOperationException("組件裡沒有嵌入 ProbeFacts.cs；快取的作廢條件靠它。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var region = Regex.Match(reader.ReadToEnd(), "(?s)// <cache-facts>.*// </cache-facts>").Value;

        if (region.Length == 0)
        {
            throw new InvalidOperationException("ProbeFacts.cs 裡找不到 <cache-facts> 區段；快取的作廢條件靠它。");
        }

        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(region))).Replace("-", string.Empty);
    }

    /// <summary>斷詞，不剖析。</summary>
    public IList<TSqlParserToken> Tokenize(string text)
    {
        return _parser.Value.GetTokenStream(new StringReader(text), out _);
    }

    /// <summary>整段剖析並交出語法樹。</summary>
    public TSqlFragment? Parse(string text, out IList<ParseError> errors)
    {
        Interlocked.Increment(ref _parses);
        return _parser.Value.Parse(new StringReader(text), out errors);
    }

    /// <summary>前綴連同字的詞元只在同一批探測裡重複用到，留著只會把記憶體吃光。</summary>
    public void ForgetHeads()
    {
        _heads.Clear();
    }

    // <cache-facts>
    // 這個區段算出的是存進快取的事實，文字一變快取就整份作廢；解讀事實的規則放在區段外。
    internal const byte Recanted = 0;
    internal const byte InWord = 1;
    internal const byte InContinuation = 2;
    internal const byte Whole = 3;

    // 每拼接這麼多次，就另外整段剖析一次比對；對不上代表拼接的前提有漏洞，快取不能留。
    private const int VerifyEvery = 64;

    // "MERGE 陳述式必須以分號結尾"，見 AcceptedFacts。
    private const int MergeNeedsSemicolon = 46097;

    // 編譯時的列舉值取自 NuGet 那一份，執行期載入的是 SSMS 那一份：依名稱取，不賭兩份的成員順序相同。
    private static readonly TSqlTokenType EndOfFile = (TSqlTokenType)Enum.Parse(typeof(TSqlTokenType), "EndOfFile");

    private readonly ConcurrentDictionary<string, TSqlParserToken[]?> _heads =
        new ConcurrentDictionary<string, TSqlParserToken[]?>(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TSqlParserToken[]?> _tails =
        new ConcurrentDictionary<string, TSqlParserToken[]?>(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _joins =
        new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
    private long _spliced;
    private volatile bool _mismatch;

    /// <summary>最早一個拒收錯誤的位置；沒有就是 int.MaxValue。不給字就是整段剖析。</summary>
    public int FirstRejection(string prefix, string word = "", string continuation = "")
    {
        return FirstRejectionIn(ParseErrors(prefix, word, continuation), int.MaxValue);
    }

    /// <summary>剖析得完、一個錯都沒有。不給字就是整段剖析。</summary>
    public bool IsComplete(string prefix, string word = "", string continuation = "")
    {
        return ParseErrors(prefix, word, continuation).Count == 0;
    }

    /// <summary>每個字接上續尾之後最早的拒收落在哪一段，每字兩個位元。</summary>
    public byte[] Classes(string probe, string[] words, string continuation)
    {
        var classes = new byte[words.Length];

        Parallel.For(0, words.Length, index =>
        {
            var word = words[index];
            var wordEnd = probe.Length + word.Length;
            var rejection = FirstRejection(probe, word, continuation);

            classes[index] =
                rejection < probe.Length ? Recanted :
                rejection <= wordEnd ? InWord :
                rejection <= wordEnd + continuation.Length ? InContinuation :
                Whole;
        });

        var packed = new byte[(words.Length + 3) / 4];

        for (var index = 0; index < words.Length; index++)
        {
            packed[index / 4] |= (byte)(classes[index] << (index % 4 * 2));
        }

        return packed;
    }

    /// <summary>第三階段的事實：最早的拒收位置（低 32 位元），以及補上分號仍報 46097（第 32 位元）。</summary>
    /// <remarks>
    /// 46097 只在 MERGE 已經完整時出現。少了分號時剖析器只報這一條，之後的字一路跳到分號都不再檢查，
    /// 動作寫完之後的格子「接受」普通名稱，非保留字的 OUTPUT 就分不出來。補上分號再剖析一次：
    /// 落在分號上的錯誤是語句沒寫完，與出現未預期的檔案結尾同義，不算；分號補上了還報 46097，
    /// 代表剖析器又跳過了一段，整段過不了。
    /// </remarks>
    public long AcceptedFacts(string prefix, string word, string continuation)
    {
        var errors = ParseErrors(prefix, word, continuation);

        if (!HasError(errors, MergeNeedsSemicolon))
        {
            return (uint)FirstRejectionIn(errors, int.MaxValue);
        }

        var retried = ParseErrors(prefix, word, continuation + ";");
        var skipped = HasError(retried, MergeNeedsSemicolon);
        var first = FirstRejectionIn(retried, prefix.Length + word.Length + continuation.Length);
        return (skipped ? 1L << 32 : 0L) | (uint)first;
    }

    /// <summary>剖析 prefix + word + continuation，結果與整段剖析相同；有字時前綴連同字、續尾各只斷詞一次。</summary>
    /// <remarks>
    /// 整段剖析一次約 80µs，改餵斷好的詞元只要約 20µs；同一個前綴配同一個字要接幾十條續尾，不必每次重新斷詞。
    /// 前綴連同那個字一起斷（字要自成一個詞元，才知道前綴沒有吃進它），續尾另外斷，接起來餵剖析器。斷詞不是處處與上下文無關：GO 只有在行首才是批次分隔，SELECT a FROM t GO 的 GO
    /// 是識別字、單獨斷卻是分隔符號，所以字與續尾連在一起斷的結果要與分開斷的相同才拼，否則整段剖析。
    /// </remarks>
    private IList<ParseError> ParseErrors(string prefix, string word, string continuation)
    {
        if (word.Length == 0)
        {
            return ParseWhole(prefix + continuation);
        }

        var head = _heads.GetOrAdd(prefix + word, key => LexHead(key, prefix.Length));
        var tail = _tails.GetOrAdd(continuation, LexTail);

        if (head == null || tail == null || !_joins.GetOrAdd(word + "\u0001" + continuation, key => JoinsCleanly(word, continuation)))
        {
            return ParseWhole(prefix + word + continuation);
        }

        // 同一份詞元會被幾個執行緒同時拿去剖析，每次複製一份，不賭剖析器不改它。
        var tokens = new List<TSqlParserToken>(head.Length + tail.Length);

        foreach (var token in head)
        {
            tokens.Add(new TSqlParserToken(token.TokenType, token.Offset, token.Text, token.Line, token.Column));
        }

        var last = head[head.Length - 1];
        var length = prefix.Length + word.Length;
        var column = last.Column + last.Text.Length;

        foreach (var token in tail)
        {
            tokens.Add(new TSqlParserToken(token.TokenType, length + token.Offset, token.Text, last.Line, column + token.Offset));
        }

        _parser.Value.Parse(tokens, out var errors);
        Interlocked.Increment(ref _parses);

        if (Interlocked.Increment(ref _spliced) % VerifyEvery == 0)
        {
            var text = prefix + word + continuation;
            var expected = ErrorSignature(ParseWhole(text));
            var actual = ErrorSignature(errors);

            if (expected != actual)
            {
                _mismatch = true;
                throw new InvalidOperationException(string.Format(
                    "拼接詞元的剖析結果與整段剖析不同，快取已刪除：{0}（整段 {1}，拼接 {2}）", text, expected, actual));
            }
        }

        return errors;
    }

    private IList<ParseError> ParseWhole(string text)
    {
        Parse(text, out var errors);
        return errors;
    }

    /// <summary>斷詞，含最後的檔案結尾詞元；有斷詞錯誤就回傳 null，交給整段剖析。</summary>
    private TSqlParserToken[]? Lex(string text, bool keepEnd)
    {
        var tokens = _parser.Value.GetTokenStream(new StringReader(text), out var errors);

        if (errors.Count > 0 || tokens.Count == 0 || tokens[tokens.Count - 1].TokenType != EndOfFile)
        {
            return null;
        }

        var result = new TSqlParserToken[keepEnd ? tokens.Count : tokens.Count - 1];

        for (var index = 0; index < result.Length; index++)
        {
            result[index] = tokens[index];
        }

        return result;
    }

    private TSqlParserToken[]? LexHead(string text, int wordStart)
    {
        var tokens = Lex(text, false);

        if (tokens == null || tokens.Length == 0)
        {
            return null;
        }

        var last = tokens[tokens.Length - 1];
        return last.Offset == wordStart && last.Text.Length == text.Length - wordStart ? tokens : null;
    }

    // 檔案結尾詞元要用斷詞器給的那一個：自己造的 Text 是空字串，剖析器在 WITHIN GROUP (GRAPH 的結尾
    // 走的路就不同（整段剖析報內部錯誤 46001，自己造的報 46010）。
    // 續尾的詞元沿用字所在的那一行，跨行的話行號對不上。
    private TSqlParserToken[]? LexTail(string continuation)
    {
        return continuation.IndexOf('\n') < 0 && continuation.IndexOf('\r') < 0 ? Lex(continuation, true) : null;
    }

    private bool JoinsCleanly(string word, string continuation)
    {
        var joined = Lex(word + continuation, true);
        var alone = Lex(word, false);
        var tail = _tails.GetOrAdd(continuation, LexTail);

        if (joined == null || alone == null || tail == null || joined.Length != alone.Length + tail.Length)
        {
            return false;
        }

        for (var index = 0; index < joined.Length; index++)
        {
            var expected = index < alone.Length ? alone[index] : tail[index - alone.Length];
            var offset = index < alone.Length ? expected.Offset : word.Length + expected.Offset;

            if (joined[index].TokenType != expected.TokenType || joined[index].Offset != offset || joined[index].Text != expected.Text)
            {
                return false;
            }
        }

        return true;
    }

    // 產生器用得到的只有每個錯誤碼最早落在哪裡；拼接與整段在錯誤復原之後報的重複錯誤次數會不同，不比。
    private static string ErrorSignature(IList<ParseError> errors)
    {
        var earliest = new SortedDictionary<int, int>();

        foreach (var error in errors)
        {
            if (!earliest.TryGetValue(error.Number, out var offset) || error.Offset < offset)
            {
                earliest[error.Number] = error.Offset;
            }
        }

        var signature = new StringBuilder();

        foreach (var pair in earliest)
        {
            signature.Append(pair.Key).Append('@').Append(pair.Value).Append(';');
        }

        return signature.ToString();
    }

    /// <summary>落在 end 之前、最早的一個拒收錯誤；沒有就是 int.MaxValue。</summary>
    private int FirstRejectionIn(IList<ParseError> errors, int end)
    {
        var first = int.MaxValue;

        foreach (var error in errors)
        {
            if (_rejecting.Contains(error.Number) && error.Offset < end && error.Offset < first)
            {
                first = error.Offset;
            }
        }

        return first;
    }

    private static bool HasError(IList<ParseError> errors, int number)
    {
        foreach (var error in errors)
        {
            if (error.Number == number)
            {
                return true;
            }
        }

        return false;
    }
    // </cache-facts>
}
