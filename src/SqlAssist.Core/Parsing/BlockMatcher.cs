using System;
using System.Collections.Generic;
using System.Threading;

namespace SqlAssist.Core.Parsing;

/// <summary>每份文字建立一次；端點與包含查詢皆為 O(log n)，祖先查詢再加 O(depth)。</summary>
public sealed class BlockMatcher
{
    private static readonly (string Open, string Close, string? Suffix, BlockKind Kind)[] Rules =
    {
        ("BEGIN", "END", "TRY", BlockKind.Try), ("BEGIN", "END", "CATCH", BlockKind.Catch),
        ("BEGIN", "END", null, BlockKind.Block), ("CASE", "END", null, BlockKind.Case)
    };
    private static readonly HashSet<string> ClosingKeywords = new(Array.ConvertAll(Rules, rule => rule.Close), StringComparer.OrdinalIgnoreCase);
    private readonly (BlockSpan Span, BlockPair Pair)[] _endpoints;
    private readonly (int Position, BlockPair? Pair)[] _regions;
    private readonly Dictionary<BlockPair, BlockPair?> _parents = new();
    public IReadOnlyList<BlockPair> Pairs { get; }

    public BlockMatcher(string sql, CancellationToken cancellationToken = default)
    {
        if (sql is null) throw new ArgumentNullException(nameof(sql));
        var tokens = SqlTokenizer.TokenizeBlocks(sql, cancellationToken);
        var pairs = new List<BlockPair>();
        var stack = new Stack<(int Rule, BlockSpan[] Opening)>();
        foreach (var pair in SqlTokenNavigator.FindParenthesisPairs(tokens, 0, tokens.Count, t => t.IsKeyword("GO")))
            pairs.Add(new BlockPair(BlockKind.Parenthesis, Spans(tokens[pair.Key]), Spans(tokens[pair.Value])));

        for (var i = 0; i < tokens.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = tokens[i];
            bool Next(string word, int offset = 1) => i + offset < tokens.Count && tokens[i + offset].IsKeyword(word);
            if (token.IsKeyword("GO")) { stack.Clear(); continue; }
            if (token.Kind == SqlTokenKind.String)
            {
                // ScriptDom 已辨認完整字串與跳脫；只取外框，不掃描內容中的引號或 SQL。
                var quote = token.Text.Length > 0 && token.Text[0] == '\'' ? 0 : 1;
                if (token.Text.Length >= quote + 2 && token.Text[quote] == '\'' && token.Text[token.Text.Length - 1] == '\'')
                    pairs.Add(new BlockPair(BlockKind.String,
                        new[] { new BlockSpan(token.Start + quote, 1) }, new[] { new BlockSpan(token.End - 1, 1) }));
                continue;
            }
            if (token.IsQuoted && token.Text.Length >= 2 && token.Text[0] == '[' && token.Text[token.Text.Length - 1] == ']')
            {
                pairs.Add(new BlockPair(BlockKind.Bracket,
                    new[] { new BlockSpan(token.Start, 1) }, new[] { new BlockSpan(token.End - 1, 1) }));
                continue;
            }

            // TRAN 不是區塊；不讓交易開頭偷走後面的 END。
            if (token.IsKeyword("BEGIN") && (Next("TRAN") || Next("TRANSACTION") ||
                Next("DIALOG") || Next("CONVERSATION") ||
                (Next("DISTRIBUTED") && (Next("TRAN", 2) || Next("TRANSACTION", 2))))) continue;

            if (!token.IsQuoted && token.Kind == SqlTokenKind.Identifier && ClosingKeywords.Contains(token.Value))
            {
                if (token.IsKeyword("END") && Next("CONVERSATION")) continue;
                string? suffix = null;
                foreach (var rule in Rules)
                    if (token.IsKeyword(rule.Close) && rule.Suffix is not null && Next(rule.Suffix)) { suffix = rule.Suffix; break; }
                var closing = suffix is not null ? Spans(token, tokens[++i]) : Spans(token);
                // 不向外找另一種開頭：編輯中的錯配不得跨越堆疊頂端亂配。
                if (stack.Count > 0 && token.IsKeyword(Rules[stack.Peek().Rule].Close) &&
                    string.Equals(Rules[stack.Peek().Rule].Suffix, suffix, StringComparison.OrdinalIgnoreCase))
                {
                    var open = stack.Pop();
                    pairs.Add(new BlockPair(Rules[open.Rule].Kind, open.Opening, closing));
                }
                continue;
            }

            for (var ruleIndex = 0; ruleIndex < Rules.Length; ruleIndex++)
            {
                var rule = Rules[ruleIndex];
                if (!token.IsKeyword(rule.Open) || (rule.Suffix is not null && !Next(rule.Suffix))) continue;
                stack.Push((ruleIndex, rule.Suffix is null ? Spans(token) : Spans(token, tokens[++i])));
                break;
            }
        }

        pairs.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
        var accepted = new List<BlockPair>(pairs.Count);
        var parents = new Stack<BlockPair>();
        var endpoints = new List<(BlockSpan Span, BlockPair Pair)>();
        var events = new List<(int Position, bool Open, BlockPair Pair)>();
        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (parents.Count > 0 && parents.Peek().Span.End <= pair.Span.Start) parents.Pop();
            // 不完整 SQL 可能讓括號與關鍵字交叉；忽略交叉者以維持查詢樹的包含關係。
            if (parents.Count > 0 && pair.Span.End > parents.Peek().Span.End) continue;
            _parents.Add(pair, parents.Count == 0 ? null : parents.Peek());
            parents.Push(pair);
            accepted.Add(pair);
            foreach (var span in pair.Opening) endpoints.Add((span, pair));
            foreach (var span in pair.Closing) endpoints.Add((span, pair));
            events.Add((pair.Span.Start, true, pair));
            events.Add((pair.Span.End, false, pair));
        }
        endpoints.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
        events.Sort((a, b) => a.Position != b.Position ? a.Position.CompareTo(b.Position) : a.Open.CompareTo(b.Open));
        var regions = new List<(int Position, BlockPair? Pair)>();
        foreach (var item in events) regions.Add((item.Position, item.Open ? item.Pair : _parents[item.Pair]));
        Pairs = accepted.AsReadOnly();
        _endpoints = endpoints.ToArray();
        _regions = regions.ToArray();
    }

    public BlockPair? FindPairAt(int position)
    {
        var index = UpperBound(_endpoints, static item => item.Span.Start, position) - 1;
        return index >= 0 && _endpoints[index].Span.Contains(position) ? _endpoints[index].Pair : null;
    }

    public BlockPair? GetEnclosingBlock(int position)
    {
        var index = UpperBound(_regions, static item => item.Position, position) - 1;
        return index < 0 ? null : _regions[index].Pair;
    }

    public IReadOnlyList<BlockPair> GetAncestors(int position)
    {
        var result = new List<BlockPair>();
        for (var pair = GetEnclosingBlock(position); pair is not null; pair = _parents[pair]) result.Add(pair);
        return result.AsReadOnly();
    }

    /// <summary>直接沿父索引找第一個符合者；游標路徑不配置整份祖先清單。</summary>
    public BlockPair? FindEnclosingBlock(int position, Func<BlockPair, bool> accepts)
    {
        if (accepts is null) throw new ArgumentNullException(nameof(accepts));
        return FindEnclosingBlock(position, accepts, static (pair, filter) => filter(pair));
    }

    /// <summary>將篩選狀態以值傳入，避免每次游標查詢都建立捕捉設定的閉包。</summary>
    public BlockPair? FindEnclosingBlock<TState>(int position, TState state, Func<BlockPair, TState, bool> accepts)
    {
        if (accepts is null) throw new ArgumentNullException(nameof(accepts));
        for (var pair = GetEnclosingBlock(position); pair is not null; pair = _parents[pair])
            if (accepts(pair, state)) return pair;
        return null;
    }

    /// <summary>只列出與半開區間重疊的區塊；供可視區域取 Tag，不遍歷整份文件。</summary>
    public IEnumerable<BlockPair> GetIntersectingBlocks(int start, int length)
    {
        if (start < 0 || length < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (length == 0) yield break;
        for (var pair = GetEnclosingBlock(start); pair is not null; pair = _parents[pair]) yield return pair;
        var index = UpperBound(Pairs, static pair => pair.Span.Start, start);
        var end = (long)start + length;
        while (index < Pairs.Count && Pairs[index].Span.Start < end) yield return Pairs[index++];
    }

    private static int UpperBound<T>(IReadOnlyList<T> items, Func<T, int> key, int position)
    {
        var low = 0;
        var high = items.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (key(items[middle]) <= position) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static BlockSpan[] Spans(params SqlToken[] tokens) =>
        Array.ConvertAll(tokens, token => new BlockSpan(token.Start, token.Length));
}
