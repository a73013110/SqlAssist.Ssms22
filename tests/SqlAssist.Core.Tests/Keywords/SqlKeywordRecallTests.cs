using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Keywords;

/// <summary>
/// 召回稽核：合法的 T-SQL 裡每一個字，在它的起點都列得出來。
/// </summary>
/// <remarks>
/// 位置分析與片語都是「這一格接哪些字」的推論，推錯的症狀是使用者要的字不見了，而那不會讓
/// 任何一個針對單一位置寫的測試失敗。這裡反過來拿真的寫得出來的語句問：語料
/// （<c>RecallCorpus.sql</c>）每一段是一句，在每一個關鍵字、片語字、資料型別、提示與日期部分的
/// 起點走一次產品的路徑，那個字要在清單裡，而且那一格的清單要開得起來。
///
/// 列不出來的寫進 <c>RecallKnownGaps.txt</c>，每一條附理由。清單裡的缺口變成列得出來時這裡也失敗：
/// 修好的缺口要從清單刪掉，清單才不會變成沒人敢動的豁免名單。
/// </remarks>
public sealed class SqlKeywordRecallTests
{
    private const string Caret = "⎵";

    [Fact]
    public void 語料裡的每一個字在它的起點列得出來()
    {
        var misses = Audit().ToList();
        var gaps = KnownGap.Load();
        var unexpected = misses.Where(miss => !gaps.Any(gap => gap.Covers(miss))).ToList();
        var fixedGaps = gaps.Where(gap => !misses.Any(gap.Covers)).ToList();

        var message = new StringBuilder();

        if (unexpected.Count > 0)
        {
            message.AppendLine("列不出來的字（修掉，或寫進 RecallKnownGaps.txt 並附理由）：");

            foreach (var miss in unexpected)
            {
                message.AppendLine("  " + miss);
            }
        }

        if (fixedGaps.Count > 0)
        {
            message.AppendLine("已經列得出來的已知缺口（從 RecallKnownGaps.txt 刪掉）：");

            foreach (var gap in fixedGaps)
            {
                message.AppendLine("  " + gap.Text);
            }
        }

        Assert.True(message.Length == 0, message.ToString());
    }

    [Fact]
    public void 已知缺口每一條都有理由()
    {
        Assert.All(KnownGap.Load(), gap => Assert.False(string.IsNullOrWhiteSpace(gap.Reason), gap.Text));
    }

    /// <summary>一個列不出來的字：它前面的詞元（以一個空白相接）與那個字。</summary>
    private readonly struct Miss
    {
        public Miss(string before, string word, bool closed)
        {
            Before = before;
            Word = word;
            Closed = closed;
        }

        public string Before { get; }

        public string Word { get; }

        /// <summary>清單根本不開（這一格被當成新名字或不可補）。</summary>
        public bool Closed { get; }

        public override string ToString() =>
            $"{Before} {Caret}{Word}{(Closed ? "（清單不開）" : string.Empty)}";
    }

    /// <summary>已知缺口：一段游標前的尾巴（詞元以一個空白相接）與該列出的字。</summary>
    private sealed class KnownGap
    {
        private KnownGap(string text, string[] before, string word, string reason)
        {
            Text = text;
            Before = before;
            Word = word;
            Reason = reason;
        }

        public string Text { get; }

        public string[] Before { get; }

        public string Word { get; }

        public string Reason { get; }

        /// <summary>缺口的尾巴是游標前詞元的後綴，字相同；沒有尾巴的缺口只認一句的第一個字。</summary>
        public bool Covers(Miss miss)
        {
            if (!string.Equals(miss.Word, Word, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Before.Length == 0)
            {
                return miss.Before.Length == 0;
            }

            var before = miss.Before.Split(' ');

            return before.Length >= Before.Length &&
                before.Skip(before.Length - Before.Length)
                    .SequenceEqual(Before, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>一行一條：<c>尾巴 ⎵字 | 理由</c>；<c>#</c> 開頭與空白行略過。</summary>
        public static IReadOnlyList<KnownGap> Load()
        {
            var gaps = new List<KnownGap>();

            foreach (var line in File.ReadAllLines(DataPath("RecallKnownGaps.txt")))
            {
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var bar = line.IndexOf(" | ", StringComparison.Ordinal);
                var entry = bar < 0 ? line : line.Substring(0, bar);
                var reason = bar < 0 ? string.Empty : line.Substring(bar + 3);
                var caret = entry.LastIndexOf(Caret, StringComparison.Ordinal);

                if (caret < 0)
                {
                    throw new FormatException($"已知缺口少了 {Caret}：{line}");
                }

                var before = entry.Substring(0, caret).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var word = entry.Substring(caret + Caret.Length).Trim();
                gaps.Add(new KnownGap(entry, before, word, reason));
            }

            return gaps;
        }
    }

    private static IEnumerable<Miss> Audit()
    {
        var audited = AuditedWords();
        var builtIn = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current);

        foreach (var statement in Statements())
        {
            var tokens = SqlTokenizer.Tokenize(statement);

            for (var index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];

                if (token.Kind != SqlTokenKind.Identifier ||
                    token.IsQuoted ||
                    (index >= 1 && tokens[index - 1].IsPunctuation(".")) ||
                    !audited.Contains(token.Value))
                {
                    continue;
                }

                if (IsOffered(statement, tokens, index, builtIn, out var closed))
                {
                    continue;
                }

                var before = string.Join(" ", tokens.Take(index).Select(item => item.Text));
                yield return new Miss(before, token.Value.ToUpperInvariant(), closed);
            }
        }
    }

    /// <summary>
    /// 第 <paramref name="index"/> 個詞元在它的起點列得出來；多字的建議項（<c>FORCE ORDER</c>、
    /// <c>GROUPING SETS</c>）在第一個字的起點列出來，後面的字也算。
    /// </summary>
    private static bool IsOffered(
        string statement,
        IReadOnlyList<SqlToken> tokens,
        int index,
        IReadOnlyList<SqlSuggestion> builtIn,
        out bool closed)
    {
        closed = false;

        for (var start = index; start >= 0 && start > index - 3; start--)
        {
            var words = tokens.Skip(start).Take(index - start + 1).Select(item => item.Value).ToArray();

            if (words.Any(word => word.Length == 0 || !char.IsLetter(word[0]) && word[0] != '_'))
            {
                break;
            }

            var context = SqlCompletionContextAnalyzer.Analyze(
                statement.Substring(0, tokens[start].Start) + tokens[start].Text.Substring(0, 1));

            if (!SqlCompletionPolicy.OffersItems(context.Slot))
            {
                closed |= start == index;
                continue;
            }

            if (SuggestionContextFilter.Filter(Candidates(context, builtIn), context)
                .Any(suggestion => StartsWithWords(suggestion.DisplayText, words)))
            {
                closed = false;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 候選清單：與 <c>SqlAsyncCompletionSource.GetCandidatesAsync</c> 同一套分派，少了資料庫那一份。
    /// </summary>
    private static IEnumerable<SqlSuggestion> Candidates(SqlCompletionContext context, IReadOnlyList<SqlSuggestion> builtIn)
    {
        var phrase = context.ClausePhrase?.Suggestions ?? Array.Empty<SqlSuggestion>();

        return context.Target switch
        {
            CompletionTarget.GlobalVariable => SqlGlobalVariableCatalog.All,
            CompletionTarget.DatePart => SqlArgumentCatalog.DateParts,
            CompletionTarget.TableHint => SqlArgumentCatalog.TableHints,
            CompletionTarget.QueryHint => SqlArgumentCatalog.QueryHints,
            CompletionTarget.ClauseKeyword => phrase,
            CompletionTarget.DataType => SqlDataTypeCatalog.All,
            _ => builtIn.Concat(phrase).Concat(context.ScriptSources)
        };
    }

    /// <summary>建議項的顯示文字以這幾個字開頭；<c>INDEX(</c>、<c>varchar(n)</c> 的括號不算字。</summary>
    private static bool StartsWithWords(string displayText, string[] words)
    {
        var parts = displayText.Split(new[] { ' ', '(' }, StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= words.Length &&
            parts.Take(words.Length).SequenceEqual(words, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 要稽核的字：關鍵字、片語的字、資料型別、資料表與查詢提示、日期部分。
    /// </summary>
    /// <remarks>
    /// 內建函式與名稱不在裡面：它們的清單來自同一條位置規則，但語料裡寫的名稱是使用者取的，
    /// 列不列得出與這一格的文法無關。全域變數、變數與定序也不是字。
    /// </remarks>
    private static HashSet<string> AuditedWords()
    {
        var words = new HashSet<string>(SqlKeywordCatalog.All, StringComparer.OrdinalIgnoreCase);

        // 片語尾巴裡的字面字也算：AT TIME 的 AT、NEXT VALUE 的 VALUE 不在任何一份清單的字裡。
        foreach (var phrase in SqlClausePhraseCatalog.All)
        {
            words.UnionWith(phrase.Words.SelectMany(word => word.Split(' ')));
            words.UnionWith(phrase.Pattern.Split(' ').Where(item => item.Length > 0 && (char.IsLetter(item[0]) || item[0] == '_')));
        }

        foreach (var suggestion in SqlDataTypeCatalog.All
            .Concat(SqlArgumentCatalog.TableHints)
            .Concat(SqlArgumentCatalog.QueryHints)
            .Concat(SqlArgumentCatalog.DateParts))
        {
            words.UnionWith(suggestion.DisplayText.Split(new[] { ' ', '(' }, StringSplitOptions.RemoveEmptyEntries));
        }

        return words;
    }

    /// <summary>語料的每一段；段與段以空白行分開。</summary>
    private static IEnumerable<string> Statements()
    {
        var text = File.ReadAllText(DataPath("RecallCorpus.sql")).Replace("\r", string.Empty);

        return text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(block => SqlTokenizer.Tokenize(block).Count > 0);
    }

    private static string DataPath(string name) => Path.Combine(AppContext.BaseDirectory, "Keywords", name);
}
