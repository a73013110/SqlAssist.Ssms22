using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;

namespace SqlAssist.CompletionAudit;

/// <summary>
/// 召回稽核：在語料每個詞元的起點截斷，走產品的路徑問「這裡列什麼」，作者寫的下一個詞就是答案。
/// </summary>
/// <remarks>
/// 不靠人挑案例，也不把 SSMS 當真值。每一格照平台實際的走法問一次：
/// <list type="bullet">
/// <item>空前綴就會自己開的位置（<see cref="SqlCompletionPolicy.IsClosed"/>），清單在那時建好，之後打字只篩選，
/// 所以拿空前綴的候選、以第一個字元篩。</item>
/// <item>其餘位置打了第一個字元才開，拿那時的候選、以第一個字元篩。</item>
/// </list>
/// 答案要在篩完、排名之後<b>看得到</b>的那一份裡（<see cref="SuggestionList"/> 有上限）；這一格根本不開清單也算漏。
///
/// 字面值、新取的名稱、查不到存在的名稱與剖析不過的句子分類排除（<see cref="AuditExclusion"/>）。比對不分大小寫、去方括號；
/// 多字的建議項在第一個字的起點列出就算（<c>GROUPING SETS</c>），多段名稱比最後一段。
/// </remarks>
public sealed class CompletionAuditor
{
    private const string Caret = "⎵";

    private const int ExampleLines = 3;

    private const int ExampleLineWidth = 160;

    private readonly SqlAssistSettings _settings;
    private readonly IReadOnlyList<SqlSuggestion> _builtIn;
    private readonly IAuditMask _mask;

    /// <param name="builtIn">關鍵字、內建函式與片段（<see cref="BuiltInSuggestionCatalog.Create"/>）。</param>
    /// <param name="mask">範例裡的名稱怎麼換成代號。</param>
    public CompletionAuditor(SqlAssistSettings settings, IReadOnlyList<SqlSuggestion> builtIn, IAuditMask mask)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _builtIn = builtIn ?? throw new ArgumentNullException(nameof(builtIn));
        _mask = mask ?? throw new ArgumentNullException(nameof(mask));
    }

    /// <summary>稽核一段語料的每一個詞元。</summary>
    public async Task<AuditResult> AuditAsync(AuditFragment fragment, IAuditCatalog catalog, CancellationToken cancellationToken)
    {
        if (fragment is null)
        {
            throw new ArgumentNullException(nameof(fragment));
        }

        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        var tally = new AuditTally();
        var misses = new List<AuditMiss>();

        foreach (var (start, length) in Batches(fragment.Text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await Batch.CreateAsync(this, fragment, start, fragment.Text.Substring(start, length), catalog, cancellationToken)
                .ConfigureAwait(false);
            tally.Unparse(batch.UnparsedStatements);

            for (var index = 0; index < batch.Tokens.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await batch.AuditAsync(index, tally, cancellationToken).ConfigureAwait(false) is { } miss)
                {
                    misses.Add(miss);
                }
            }
        }

        return new AuditResult(misses, tally);
    }

    /// <summary>重驗一段語料裡的一個位置；還漏就回傳那一筆，列得出來或已經不算答案時回傳 null。</summary>
    /// <param name="tally">那一個位置的計數；不算答案時 <see cref="AuditTally.Excluded"/> 記著為什麼。</param>
    public async Task<AuditMiss?> RecheckAsync(
        AuditFragment fragment,
        int offset,
        IAuditCatalog catalog,
        AuditTally tally,
        CancellationToken cancellationToken)
    {
        if (fragment is null)
        {
            throw new ArgumentNullException(nameof(fragment));
        }

        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        if (tally is null)
        {
            throw new ArgumentNullException(nameof(tally));
        }

        foreach (var (start, length) in Batches(fragment.Text))
        {
            if (offset < start || offset >= start + length)
            {
                continue;
            }

            var batch = await Batch.CreateAsync(this, fragment, start, fragment.Text.Substring(start, length), catalog, cancellationToken)
                .ConfigureAwait(false);
            var index = batch.IndexAt(offset - start);

            return index < 0
                ? null
                : await batch.AuditAsync(index, tally, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>以 <c>GO</c> 切開的批次：起點與長度。</summary>
    /// <remarks>產品分析的是整份編輯器文字，但 <c>GO</c> 之後的範圍、變數與暫存資料表都重新開始，逐批問的答案相同。</remarks>
    private static IEnumerable<(int Start, int Length)> Batches(string text)
    {
        var start = 0;

        foreach (var token in SqlTokenizer.Tokenize(text))
        {
            if (!token.IsKeyword("GO"))
            {
                continue;
            }

            if (token.Start > start)
            {
                yield return (start, token.Start - start);
            }

            start = token.End;
        }

        if (text.Length > start)
        {
            yield return (start, text.Length - start);
        }
    }

    private async Task<IReadOnlyList<SqlSuggestion>> CandidatesAsync(
        SqlCompletionContext context,
        IAuditCatalog catalog,
        CancellationToken cancellationToken)
    {
        // 與建議來源同一個順序：先用連線的名單認出限定字，欄位預熱完再問清單。產品的預熱在背景跑、
        // 下一鍵才命中；稽核看的是穩定狀態，等它做完。
        var metadata = catalog.Metadata;
        context = await metadata.ResolveQualifierAsync(context, cancellationToken).ConfigureAwait(false);
        await metadata.WarmColumnsAsync(context.ScopeSources, cancellationToken).ConfigureAwait(false);
        return await SqlCompletionCandidates
            .GetAsync(context, _builtIn, _settings, metadata, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>一個詞元的起點問到的清單。</summary>
    private sealed class Probe
    {
        public static readonly Probe Closed = new(Array.Empty<SqlSuggestion>(), Array.Empty<SqlSuggestion>(), closed: true);

        public Probe(IReadOnlyList<SqlSuggestion> candidates, IReadOnlyList<SqlSuggestion> visible, bool closed = false)
        {
            Candidates = candidates;
            Visible = visible;
            IsClosed = closed;
        }

        public IReadOnlyList<SqlSuggestion> Candidates { get; }

        public IReadOnlyList<SqlSuggestion> Visible { get; }

        public bool IsClosed { get; }
    }

    /// <summary>一個批次：詞元、新取的名稱、名稱索引，以及每個起點問過的清單。</summary>
    private sealed class Batch
    {
        private readonly CompletionAuditor _owner;
        private readonly AuditFragment _fragment;
        private readonly int _offset;
        private readonly string _text;
        private readonly IAuditCatalog _catalog;
        private readonly AuditDefinitions _definitions;
        private readonly IAuditNameIndex _index;
        private readonly IReadOnlyList<int> _heads;
        private readonly Dictionary<int, Probe> _probes = new();
        private readonly Shape[] _shapes;
        private readonly bool[] _placeholders;

        private Batch(
            CompletionAuditor owner,
            AuditFragment fragment,
            int offset,
            string text,
            IReadOnlyList<SqlToken> tokens,
            IAuditCatalog catalog,
            IAuditNameIndex index)
        {
            _owner = owner;
            _fragment = fragment;
            _offset = offset;
            _text = text;
            _catalog = catalog;
            _index = index;
            Tokens = tokens;
            _heads = SqlStatementHeads.Find(text, tokens);
            _definitions = AuditDefinitions.Collect(text, tokens, _heads);
            _shapes = new Shape[tokens.Count];
            _placeholders = FindPlaceholders();

            for (var position = 0; position < tokens.Count; position++)
            {
                _shapes[position] = Exclude(position, Classify(position));
            }
        }

        public IReadOnlyList<SqlToken> Tokens { get; }

        /// <summary>剖析不過、從錯的那個詞起不稽核的句數。</summary>
        public int UnparsedStatements => _definitions.UnparsedStatements;

        public static async Task<Batch> CreateAsync(
            CompletionAuditor owner,
            AuditFragment fragment,
            int offset,
            string text,
            IAuditCatalog catalog,
            CancellationToken cancellationToken)
        {
            var tokens = SqlTokenizer.Tokenize(text);
            var index = await catalog.IndexAsync(text, tokens, cancellationToken).ConfigureAwait(false);
            return new Batch(owner, fragment, offset, text, tokens, catalog, index);
        }

        public int IndexAt(int start)
        {
            for (var position = 0; position < Tokens.Count; position++)
            {
                if (Tokens[position].Start == start)
                {
                    return position;
                }
            }

            return -1;
        }

        /// <summary>稽核第 <paramref name="index"/> 個詞元；漏了回傳那一筆。</summary>
        public async Task<AuditMiss?> AuditAsync(int index, AuditTally tally, CancellationToken cancellationToken)
        {
            var shape = _shapes[index];

            if (shape.Exclusion is { } exclusion)
            {
                tally.Exclude(exclusion);
                return null;
            }

            if (shape.Class is not { } tokenClass)
            {
                return null;
            }

            tally.Audit(tokenClass);
            var token = Tokens[index];
            var probe = await ProbeAsync(index, cancellationToken).ConfigureAwait(false);

            if (tokenClass == AuditTokenClass.Word
                ? await OffersWordAsync(index, cancellationToken).ConfigureAwait(false)
                : probe.Visible.Any(item => IsName(item, token)))
            {
                return null;
            }

            var kind = probe.IsClosed
                ? AuditMissKind.Closed
                : probe.Candidates.Any(item => tokenClass == AuditTokenClass.Word
                    ? AuditText.StartsWithWords(item.DisplayText, new[] { token.Value })
                    : IsName(item, token))
                    ? AuditMissKind.Hidden
                    : AuditMissKind.Absent;

            tally.Miss();
            return CreateMiss(index, tokenClass, kind);
        }

        /// <summary>
        /// 字在它的起點列得出來；多字的建議項（<c>FORCE ORDER</c>、<c>GROUPING SETS</c>）
        /// 在第一個字的起點列出來，後面的字也算。
        /// </summary>
        private async Task<bool> OffersWordAsync(int index, CancellationToken cancellationToken)
        {
            for (var start = index; start >= 0 && start > index - 3; start--)
            {
                if (!IsWordShaped(start))
                {
                    break;
                }

                var words = Tokens.Skip(start).Take(index - start + 1).Select(item => item.Value).ToArray();
                var probe = await ProbeAsync(start, cancellationToken).ConfigureAwait(false);

                if (probe.Visible.Any(item => AuditText.StartsWithWords(item.DisplayText, words)))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsWordShaped(int index)
        {
            var token = Tokens[index];
            return token.Kind == SqlTokenKind.Identifier &&
                !token.IsQuoted &&
                token.Value.Length > 0 &&
                (char.IsLetter(token.Value[0]) || token.Value[0] == '_');
        }

        private static bool IsName(SqlSuggestion suggestion, SqlToken token) =>
            string.Equals(AuditText.LastPart(suggestion.DisplayText), AuditText.Normalize(token.Text), StringComparison.Ordinal);

        /// <summary>第 <paramref name="index"/> 個詞元的起點問到的清單；同一個起點只問一次。</summary>
        private async Task<Probe> ProbeAsync(int index, CancellationToken cancellationToken)
        {
            if (_probes.TryGetValue(index, out var cached))
            {
                return cached;
            }

            var token = Tokens[index];
            var prefix = _text.Substring(0, token.Start);
            var typed = FirstTyped(token);
            // 與建議來源同一個多載：單一參數那一支只給按鍵路徑判斷要不要參與，不做範圍分析。
            var opened = SqlCompletionContextAnalyzer.Analyze(prefix, prefix.Length);
            IReadOnlyList<SqlSuggestion> candidates;
            string typedText;

            if (SqlCompletionPolicy.OffersItems(opened.Slot) && SqlCompletionPolicy.IsClosed(opened))
            {
                // 空前綴就開的清單在那時建好，打第一個字元只是篩選。
                candidates = await _owner.CandidatesAsync(opened, _catalog, cancellationToken).ConfigureAwait(false);
                typedText = SuggestionList.TypedText(typed, fieldDefault: null);
            }
            else
            {
                var typing = SqlCompletionContextAnalyzer.Analyze(prefix + typed, prefix.Length + typed.Length);

                if (!SqlCompletionPolicy.Participates(typing, _owner._settings.TriggerAfterCharacters))
                {
                    return _probes[index] = Probe.Closed;
                }

                candidates = await _owner.CandidatesAsync(typing, _catalog, cancellationToken).ConfigureAwait(false);
                typedText = SuggestionList.TypedText((prefix + typed).Substring(typing.TokenStart), fieldDefault: null);
            }

            var sorted = SuggestionList.Sort(candidates, static item => item);
            var visible = SuggestionList
                .Update(sorted, static item => item.DisplayText, static item => item, typedText, default, cancellationToken)
                .Items
                .Select(entry => entry.Item)
                .ToArray();

            return _probes[index] = new Probe(candidates, visible);
        }

        /// <summary>
        /// 使用者打下的第一個字元；方括號與雙引號名稱連同第一個字母，全域變數是整個 <c>@@</c>。
        /// </summary>
        /// <remarks>只打一個 <c>@</c> 時問的是區域變數，那一份本來就不該有 <c>@@ROWCOUNT</c>。</remarks>
        private static string FirstTyped(SqlToken token)
        {
            var text = token.Text;
            var length = text.StartsWith("@@", StringComparison.Ordinal) || text.Length >= 2 && (text[0] == '[' || text[0] == '"') ? 2 : 1;
            return text.Substring(0, Math.Min(length, text.Length));
        }

        private Shape Classify(int index)
        {
            var token = Tokens[index];

            switch (token.Kind)
            {
                case SqlTokenKind.Number:
                case SqlTokenKind.String:
                    return new Shape("‹lit›", exclusion: AuditExclusion.Literal);
                case SqlTokenKind.Variable:
                    return ClassifyName(token, qualified: false);
                case SqlTokenKind.Identifier:
                    return ClassifyIdentifier(index, token);
                default:
                    return new Shape(token.Text);
            }
        }

        private Shape ClassifyIdentifier(int index, SqlToken token)
        {
            var afterDot = index >= 1 && Tokens[index - 1].IsPunctuation(".");

            if (token.IsKeyword("GO"))
            {
                return new Shape(token.Text);
            }

            // 觸發程序與 OUTPUT 子句的 inserted／deleted：不是誰取的名字，形狀寫出字面、不遮，自成一群。
            if (!afterDot && _definitions.IsChangeTable(token.Value, token.Start))
            {
                return new Shape(AuditText.Normalize(token.Value), AuditTokenClass.ScriptName);
            }

            // 守大寫慣例的語料看寫法；其餘看語法樹：站在資料行或多段名稱位置上的 name、type 是名稱，不是字。
            if (!_definitions.IsDefinition(token.Start) &&
                !token.IsQuoted &&
                !afterDot &&
                _index.Find(token.Value) is null &&
                !_definitions.IsDefinedBefore(token.Value, token.Start) &&
                (_fragment.WordsInUpperCase
                    ? AuditWords.Contains(token.Value) || AuditWords.IsWrittenAsWord(token.Text)
                    : AuditWords.Contains(token.Value) && !_definitions.IsNameReference(token.Start)))
            {
                return new Shape(token.Value.ToUpperInvariant(), AuditTokenClass.Word);
            }

            return ClassifyName(token, afterDot);
        }

        private Shape ClassifyName(SqlToken token, bool qualified)
        {
            if (_definitions.IsDefinition(token.Start))
            {
                return new Shape("‹new›", exclusion: AuditExclusion.NewName, masked: true, maskClass: AuditTokenClass.ScriptName);
            }

            if (token.Kind == SqlTokenKind.Variable && token.Text.StartsWith("@@", StringComparison.Ordinal))
            {
                return new Shape(token.Text.ToUpperInvariant(), AuditTokenClass.GlobalVariable);
            }

            if (_definitions.IsDefinedBefore(token.Text, token.Start, qualified))
            {
                // 外層取過同名的，這裡指的卻是內層之後才取的那一個：形狀照舊，只加排除。
                return _definitions.IsDefinedLater(token.Text, token.Start, qualified)
                    ? Name(AuditTokenClass.ScriptName).Excluded(AuditExclusion.Truncated)
                    : Name(AuditTokenClass.ScriptName);
            }

            // 名稱索引以名稱查，不分段：a.Id 的 Id 是欄位，dbo.Loan 的 Loan 是物件。
            if (_index.Find(token.Value) is { } known)
            {
                return Name(known);
            }

            var exclusion = _definitions.IsDefinedLater(token.Text, token.Start) ? AuditExclusion.Truncated : AuditExclusion.Unresolved;
            return new Shape("‹name›", exclusion: exclusion, masked: true);
        }

        /// <summary>
        /// 名稱存在還不夠：點號之後要限定字（或別名指的資料表）也認得出來，欄位要它可能屬於的資料表至少認得一張，
        /// 選取清單裡要資料來源已經寫出來。
        /// 形狀照舊，只加排除：簽章與範例要跟沒排除時一樣，後面的詞才留在原來那一群。
        /// </summary>
        private Shape Exclude(int position, Shape shape)
        {
            if (shape.Class is not { } tokenClass)
            {
                return shape;
            }

            if (_placeholders[position])
            {
                return shape.Excluded(AuditExclusion.Placeholder);
            }

            if (_definitions.IsUnparsed(Tokens[position].Start))
            {
                return shape.Excluded(AuditExclusion.Unparsed);
            }

            if (tokenClass is AuditTokenClass.Word or AuditTokenClass.GlobalVariable)
            {
                return shape;
            }

            var token = Tokens[position];

            // INSERT INTO Other.dbo.Loan (CopyNo)、FROM Other.dbo.Loan GROUP BY CopyNo：名稱索引只比名字，
            // 欄位可能屬於的表全都查不到時，碰巧同名的欄位不算列得出來。
            if (_definitions.ColumnOwners(token.Start) is { } owners && owners.All(owner => IsUnresolved(owner, token.Start)))
            {
                return shape.Excluded(AuditExclusion.Unresolved);
            }

            if (position >= 2 && Tokens[position - 1].IsPunctuation("."))
            {
                var qualifier = Tokens[position - 2];

                if (_shapes[position - 2].Exclusion is AuditExclusion.Unresolved or AuditExclusion.Truncated)
                {
                    return shape.Excluded(_shapes[position - 2].Exclusion!.Value);
                }

                // 限定字是 CTE 名稱時要這個查詢的 FROM 寫出它；遞迴 CTE 第二段的選取清單寫在 FROM 之前，
                // 截斷處還沒有來源，與之後才取的別名同一個盲點。
                if (_definitions.NamesTable(qualifier.Text, qualifier.Start) && _definitions.NeedsLaterFrom(qualifier.Start))
                {
                    return shape.Excluded(AuditExclusion.Truncated);
                }

                return _shapes[position - 2].Class == AuditTokenClass.ScriptName &&
                    _definitions.SourceOf(qualifier.Text, qualifier.Start) is { } source &&
                    IsUnresolved(source, qualifier.Start)
                        ? shape.Excluded(AuditExclusion.Unresolved)
                        : shape;
            }

            // 物件、變數與 CTE 名稱不靠 FROM 就列得出來；要 FROM 的只有欄位，以及當限定字的 CTE 名稱。
            var column = tokenClass == AuditTokenClass.Column ||
                tokenClass == AuditTokenClass.ScriptName && _definitions.IsColumnDefinedBefore(token.Text, token.Start);
            var qualifiesTable = position + 1 < Tokens.Count &&
                Tokens[position + 1].IsPunctuation(".") &&
                _definitions.NamesTable(token.Text, token.Start);

            return (column || qualifiesTable) && token.Kind == SqlTokenKind.Identifier && _definitions.NeedsLaterFrom(token.Start)
                ? shape.Excluded(AuditExclusion.Truncated)
                : shape;
        }

        /// <summary>
        /// 資料表查不到存在：名稱索引沒有、指令碼在 <paramref name="start"/> 之前也沒取過（暫存資料表、CTE），
        /// 或寫出來的資料庫不在連線的伺服器上。
        /// </summary>
        private bool IsUnresolved((string Name, string? Database) table, int start) =>
            _index.Find(table.Name) is null && !_definitions.IsDefinedBefore(table.Name, start) ||
            table.Database is { Length: > 0 } database && _index.Find(database) != AuditTokenClass.Database;

        /// <summary>佔位符起到那一句結束的詞元。</summary>
        private bool[] FindPlaceholders()
        {
            var marked = new bool[Tokens.Count];

            for (var position = 0; position < Tokens.Count; position++)
            {
                if (!IsPlaceholder(position))
                {
                    continue;
                }

                var next = _heads.Where(head => head > position).DefaultIfEmpty(Tokens.Count).First();

                for (; position < next; position++)
                {
                    marked[position] = true;
                }

                position--;
            }

            return marked;
        }

        /// <summary>
        /// 範例的佔位符：緊貼的 <c>&lt;</c>、名稱、<c>&gt;</c>，只有一個名稱（<c>&lt;login_name&gt;</c>）或帶逗號
        /// （範本的 <c>&lt;Author,,Name&gt;</c>），不跨行。比較運算長不成這樣：<c>a &lt;b AND c&gt; d</c>
        /// 中間不只一個名稱，也沒有逗號。
        /// </summary>
        private bool IsPlaceholder(int position)
        {
            var open = Tokens[position];

            if (open.Kind != SqlTokenKind.Operator ||
                open.Text != "<" ||
                position + 1 >= Tokens.Count ||
                Tokens[position + 1].Kind != SqlTokenKind.Identifier ||
                Tokens[position + 1].Start != open.End)
            {
                return false;
            }

            var names = 0;
            var commas = 0;

            for (var index = position + 1; index < Tokens.Count; index++)
            {
                var token = Tokens[index];

                if (_text.IndexOf('\n', open.End, token.End - open.End) >= 0)
                {
                    return false;
                }

                if (token.Kind == SqlTokenKind.Operator && token.Text == ">")
                {
                    return token.Start == Tokens[index - 1].End && (commas > 0 || names == 1);
                }

                if (token.IsPunctuation(","))
                {
                    commas++;
                }
                else if (token.Kind == SqlTokenKind.Identifier)
                {
                    names++;
                }
                else
                {
                    return false;
                }
            }

            return false;
        }

        private static Shape Name(AuditTokenClass tokenClass) =>
            new($"‹{tokenClass}›", tokenClass, masked: true, maskClass: tokenClass);

        private AuditMiss CreateMiss(int index, AuditTokenClass tokenClass, AuditMissKind kind)
        {
            var token = Tokens[index];
            var previous = string.Join(" ", Enumerable.Range(index - 2, 2).Select(position => position < 0 ? "^" : _shapes[position].Text));
            var signature = new AuditSignature($"{previous} {Caret}{_shapes[index].Text} [{kind}]");
            var head = _heads.LastOrDefault(position => position <= index);

            return new AuditMiss(
                _fragment,
                _offset + token.Start,
                token.Text,
                tokenClass,
                kind,
                signature,
                Render(head, index, masked: false),
                Render(head, index, masked: true),
                token.Start - Tokens[head].Start);
        }

        /// <summary>那一句從開頭到游標、最多三行，游標後面接答案。</summary>
        private string Render(int head, int index, bool masked)
        {
            var builder = new StringBuilder();

            // 那一句前面緊鄰的註解也留下來：註解尾巴的字曾經被當成限定字，少了它看不出為什麼漏。
            var before = head > 0 ? Tokens[head - 1].End : 0;
            AppendGap(builder, _text.Substring(before, Tokens[head].Start - before), masked, leading: true);

            for (var position = head; position <= index; position++)
            {
                if (position > head)
                {
                    AppendGap(builder, _text.Substring(Tokens[position - 1].End, Tokens[position].Start - Tokens[position - 1].End), masked, leading: false);
                }

                if (position == index)
                {
                    builder.Append(Caret);
                }

                builder.Append(Display(position, masked));
            }

            var lines = builder.ToString().Split('\n');
            return string.Join("\n", lines
                .Skip(Math.Max(0, lines.Length - ExampleLines))
                .Select(line => line.Length <= ExampleLineWidth ? line : "…" + line.Substring(line.Length - ExampleLineWidth)));
        }

        /// <summary>
        /// 詞元之間的空白收成一個空白或換行；註解留一個記號，內容只在原名版裡（註解常寫著真實名稱）。
        /// </summary>
        /// <param name="leading">句首之前的那一段：沒有註解就什麼都不留。</param>
        private static void AppendGap(StringBuilder builder, string gap, bool masked, bool leading)
        {
            var space = false;
            var lineBreak = false;
            var wrote = false;

            void Separate()
            {
                if ((lineBreak || space) && (wrote || !leading))
                {
                    builder.Append(lineBreak ? "\n" : " ");
                }

                space = lineBreak = false;
            }

            for (var position = 0; position < gap.Length;)
            {
                var line = position + 1 < gap.Length && gap[position] == '-' && gap[position + 1] == '-';
                var block = position + 1 < gap.Length && gap[position] == '/' && gap[position + 1] == '*';

                if (line || block)
                {
                    var end = line ? gap.IndexOf('\n', position) : gap.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    end = end < 0 ? gap.Length : line ? end : end + 2;
                    Separate();
                    builder.Append(masked ? line ? "--…" : "/*…*/" : gap.Substring(position, end - position).TrimEnd('\r'));
                    wrote = true;
                    position = end;
                    continue;
                }

                lineBreak |= gap[position] == '\n';
                space |= char.IsWhiteSpace(gap[position]);
                position++;
            }

            if (!leading || wrote)
            {
                Separate();
            }
        }

        private string Display(int index, bool masked)
        {
            var token = Tokens[index];
            var shape = _shapes[index];

            if (!masked)
            {
                return token.Text;
            }

            return token.Kind switch
            {
                SqlTokenKind.String => "'…'",
                SqlTokenKind.Number => "#",
                _ => shape.Masked ? _owner._mask.Mask(token.Text, shape.MaskClass) : token.Text,
            };
        }
    }

    /// <summary>一個詞元在簽章與範例裡的樣子，以及它要不要稽核。</summary>
    private readonly struct Shape
    {
        public Shape(
            string text,
            AuditTokenClass? tokenClass = null,
            AuditExclusion? exclusion = null,
            bool masked = false,
            AuditTokenClass? maskClass = null)
        {
            Text = text;
            Class = tokenClass;
            Exclusion = exclusion;
            Masked = masked;
            MaskClass = maskClass;
        }

        /// <summary>簽章用的形狀：字原樣大寫，名稱只留種類，常值一律 ‹lit›。</summary>
        public string Text { get; }

        /// <summary>要稽核時的種類；標點與排除的是 null。</summary>
        public AuditTokenClass? Class { get; }

        public AuditExclusion? Exclusion { get; }

        /// <summary>範例裡要換成代號。</summary>
        public bool Masked { get; }

        public AuditTokenClass? MaskClass { get; }

        /// <summary>同一個樣子、不稽核。</summary>
        public Shape Excluded(AuditExclusion exclusion) => new(Text, exclusion: exclusion, masked: Masked, maskClass: MaskClass);
    }
}
