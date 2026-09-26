using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 一段「游標前面的尾巴」，以及文法在那之後接得上的字。
/// </summary>
/// <remarks>
/// <c>SET STATISTICS </c> 之後只接 <c>IO</c>、<c>TIME</c>、<c>XML</c>、<c>PROFILE</c>，
/// <c>ALTER INDEX i ON t </c> 之後只接 <c>REBUILD</c>、<c>REORGANIZE</c>…——這些字
/// ScriptDom 掃成識別字，關鍵字目錄收不到，位置旗標也切不到這麼細。片語與它的字由
/// <c>tools/Generate-Keywords.ps1</c> 以剖析器探測產生，這裡只負責比對與提供建議項。
///
/// 比對成立時，這一格的關鍵字<b>只</b>來自片語：<see cref="SqlKeywordPosition"/> 是給整個
/// 子句的粗分層，片語是比它更靠近游標的答案。<see cref="IsClosed"/> 再決定名稱與函式還
/// 能不能一起出現。
/// </remarks>
public sealed class SqlClausePhrase
{
    private readonly Element[] _elements;
    private readonly SqlLanguageCache<IReadOnlyList<SqlSuggestion>> _suggestions;

    internal SqlClausePhrase(string pattern, string probe, bool isClosed, bool endsStatement, string[] words)
    {
        Pattern = pattern;
        Probe = probe;
        IsClosed = isClosed;
        EndsStatement = endsStatement;
        Words = words;
        StartsStatement = pattern.StartsWith("^", StringComparison.Ordinal);
        _elements = Parse(StartsStatement ? pattern.Substring(1) : pattern);
        _suggestions = new SqlLanguageCache<IReadOnlyList<SqlSuggestion>>(_ => BuildSuggestions());
    }

    /// <summary>片語的尾巴，寫法見 <c>tools/Generate-Keywords.ps1</c> 的 <c>$ClausePhrases</c>。</summary>
    public string Pattern { get; }

    /// <summary>
    /// 那裡除了 <see cref="Words"/> 沒有別的東西是對的：剖析器在這一格不收任何名稱。
    /// </summary>
    /// <remarks>
    /// 字可以是零個：<c>SET ROWCOUNT </c> 之後要的是數字，清單一項都不該有。
    /// 不封閉的片語（<c>SET IDENTITY_INSERT </c> 之後是資料表）只換掉關鍵字，名稱照常。
    /// </remarks>
    public bool IsClosed { get; }

    /// <summary>
    /// 語句寫到片語為止已經完整（<c>CREATE INDEX i ON t (a) </c>、<c>OFFSET 10 ROWS </c>）。
    /// </summary>
    /// <remarks>
    /// 這種片語的字不含下一句的開頭（產生器扣掉了），所以換了行就不算數：
    /// 換行之後的那一格更可能是下一句，那裡要的是語句開頭的整份清單。
    /// </remarks>
    public bool EndsStatement { get; }

    /// <summary>接得上的字，依產生器的順序。</summary>
    public IReadOnlyList<string> Words { get; }

    /// <summary>這些字的建議項；說明是目前介面語言的。</summary>
    public IReadOnlyList<SqlSuggestion> Suggestions => _suggestions.Current;

    /// <summary>產生器探測用的文字；測試拿它回驗比對。</summary>
    internal string Probe { get; }

    /// <summary>片語的第一個字必須是一句的開頭（<c>^</c>）。</summary>
    internal bool StartsStatement { get; }

    /// <summary>片語最後一項是字面值時的那個字；比對前先用它分桶。</summary>
    internal string? LastWord =>
        _elements[_elements.Length - 1] is { Kind: ElementKind.Word } last ? last.Word : null;

    /// <summary>片語有幾項；同時比對得上時，項數多的比較靠近游標的意思。</summary>
    internal int Length => _elements.Length;

    /// <summary>
    /// <paramref name="tokens"/> 的尾端是不是這個片語；是的話回傳片語第一個詞元的索引，否則 -1。
    /// </summary>
    internal int MatchTail(IReadOnlyList<SqlToken> tokens)
    {
        var index = tokens.Count - 1;

        for (var element = _elements.Length - 1; element >= 0; element--)
        {
            if (index < 0)
            {
                return -1;
            }

            index = _elements[element].MatchBackward(tokens, index);

            if (index == Element.Mismatch)
            {
                return -1;
            }
        }

        return index + 1;
    }

    private IReadOnlyList<SqlSuggestion> BuildSuggestions()
    {
        // 右側說明只寫種類，與目錄裡的關鍵字相同。Tag 指回片語：過濾靠它分辨
        // 「這是這個片語的字」，而不是比對顯示文字——READ 同時在關鍵字目錄與片語裡。
        var keywordKind = SqlKindText.Keyword;
        var suggestions = new SqlSuggestion[Words.Count];

        for (var index = 0; index < suggestions.Length; index++)
        {
            var word = Words[index];
            suggestions[index] = new SqlSuggestion(word, word, keywordKind, word, SuggestionKind.Keyword, tag: this);
        }

        return suggestions;
    }

    private static Element[] Parse(string pattern)
    {
        var parts = pattern.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var elements = new Element[parts.Length];

        for (var index = 0; index < parts.Length; index++)
        {
            elements[index] = parts[index] switch
            {
                "{name}" => new Element(ElementKind.Name),
                "{value}" => new Element(ElementKind.Value),
                "()" => new Element(ElementKind.Group),
                "(*" when index == parts.Length - 1 => new Element(ElementKind.OpenList),
                "(*" => throw new FormatException($"Phrase '{pattern}': (* must be the last element."),
                _ when parts[index].IndexOfAny(new[] { '{', '(', ')' }) >= 0 =>
                    throw new FormatException($"Phrase '{pattern}': unknown element {parts[index]}."),
                _ => new Element(ElementKind.Word, parts[index])
            };
        }

        if (elements.Length == 0)
        {
            throw new FormatException("Phrase must not be empty.");
        }

        return elements;
    }

    private enum ElementKind
    {
        Word,
        Name,
        Value,
        Group,
        OpenList
    }

    private readonly struct Element
    {
        public Element(ElementKind kind, string? word = null)
        {
            Kind = kind;
            Word = word;
        }

        public ElementKind Kind { get; }

        public string? Word { get; }

        /// <summary>比對不成立。不用 -1：那是「比對到指令碼開頭」，是成立的。</summary>
        public const int Mismatch = -2;

        /// <summary>
        /// 從 <paramref name="last"/> 往回比對這一項；成立時回傳這一項前面那個詞元的索引，
        /// 否則 <see cref="Mismatch"/>。
        /// </summary>
        public int MatchBackward(IReadOnlyList<SqlToken> tokens, int last)
        {
            if (last < 0)
            {
                return Mismatch;
            }

            var token = tokens[last];

            switch (Kind)
            {
                case ElementKind.Word:
                    return token.IsKeyword(Word!) && !(last >= 1 && tokens[last - 1].IsPunctuation("."))
                        ? last - 1
                        : Mismatch;

                case ElementKind.Name:
                    // 保留字也收：ALTER INDEX ALL ON t、ALTER DATABASE CURRENT 在名稱那一格寫的就是
                    // 保留字，而片語前後的字面值已經把位置釘住了，不必靠名稱這一格再擋。
                    return token.Kind == SqlTokenKind.Identifier
                        ? SqlTokenNavigator.SkipQualifiedNameBackward(tokens, last) - 1
                        : Mismatch;

                case ElementKind.Value:
                    if (token.Kind is SqlTokenKind.Number or SqlTokenKind.String or SqlTokenKind.Variable)
                    {
                        return last - 1;
                    }

                    return MatchGroup(tokens, last);

                case ElementKind.Group:
                    return MatchGroup(tokens, last);

                default:
                    if (!token.IsPunctuation("(") && !token.IsPunctuation(","))
                    {
                        return Mismatch;
                    }

                    var open = SqlTokenNavigator.FindUnclosedParenthesis(tokens, last);
                    return open >= 0 ? open - 1 : Mismatch;
            }
        }

        private static int MatchGroup(IReadOnlyList<SqlToken> tokens, int last)
        {
            if (!tokens[last].IsPunctuation(")"))
            {
                return Mismatch;
            }

            var open = SqlTokenNavigator.FindOpeningParenthesis(tokens, last);
            return open >= 0 ? open - 1 : Mismatch;
        }
    }
}
