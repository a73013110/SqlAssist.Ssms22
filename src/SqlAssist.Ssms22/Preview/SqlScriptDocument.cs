using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Ssms22.Preview;

internal enum ScriptResource
{
    FontFamily,
    FontSize,
    Background,
    Foreground,
    Keyword,
    Comment,
    String,
    Number,

    /// <summary>命中那幾個字的底色；實色，蓋過底下的語法著色。</summary>
    Highlight,

    /// <summary>命中那幾個字的字色；實色底蓋掉分類色之後，字要自己顧對比。</summary>
    HighlightForeground,

    /// <summary>
    /// <b>目前</b>停在的那一處命中的底色；比 <see cref="Highlight"/> 更重。
    /// </summary>
    /// <remarks>
    /// 兩級而不是一級，是因為「哪幾處對上了」與「我現在在第幾處」是兩個問題。只有一級的
    /// 症狀是按了「下一個命中」之後畫面捲了，而使用者要在七塊一模一樣的底色裡自己找出
    /// 剛才跳到的是哪一塊。兩級的差距由 <see cref="UI.MatchPalette"/> 保證。
    /// </remarks>
    HighlightCurrent,

    /// <summary>目前那一處命中的字色。</summary>
    HighlightCurrentForeground
}

/// <summary>
/// 把 T-SQL 指令碼排成帶語法著色的流程文件。
/// </summary>
/// <remarks>
/// 原本這裡內嵌的是一個真正的唯讀編輯器。它確實能著色也能拉選，
/// 但點進去之後編輯器會判定自己失去了聚合焦點，整個浮動視窗被平台收掉——
/// 而同一個視窗裡的資料格分頁完全沒有這個問題。差別就在內嵌編輯器
/// 會把鍵盤焦點搬進另一個呈現來源，一般的 WPF 控制項不會。
///
/// 改用 <see cref="System.Windows.Controls.RichTextBox"/>：選取、Ctrl+C 與右鍵選單
/// 都是 WPF 原生行為，焦點也留在同一棵樹裡。顏色與字型改向編輯器的
/// 分類外觀對應表借；主題變更只更新資源，不重新建立文件。
/// </remarks>
internal static class SqlScriptDocument
{
    private sealed class SourceSpan
    {
        public SourceSpan(int start, int length) { Start = start; Length = length; }
        public int Start { get; }
        public int Length { get; }
    }
    private static readonly ConditionalWeakTable<Inline, SourceSpan> SourceSpans = new();

    /// <summary>
    /// 把一處命中換成「目前」的樣子，或換回一般的樣子。
    /// </summary>
    /// <remarks>
    /// 換的是<b>資源鍵</b>而不是筆刷：切換主題時 <c>SqlScriptTheme</c> 只更新資源而不重建文件，
    /// 保存一次性筆刷的那一版會留著上一個主題的顏色。
    ///
    /// 兩級都蓋掉分類色：留住著色與一眼看得出來互斥，理由見 <see cref="UI.MatchPalette"/>。
    /// 字重再分一級，狀態就不是只靠顏色表達——高對比與色覺差異都還讀得出「我在第幾處」。
    /// </remarks>
    public static void SetCurrentMatch(IReadOnlyList<Run> runs, bool current)
    {
        if (runs is null) throw new ArgumentNullException(nameof(runs));

        foreach (var run in runs)
        {
            run.SetResourceReference(
                TextElement.BackgroundProperty,
                current ? ScriptResource.HighlightCurrent : ScriptResource.Highlight);
            run.SetResourceReference(
                TextElement.ForegroundProperty,
                current ? ScriptResource.HighlightCurrentForeground : ScriptResource.HighlightForeground);
            run.FontWeight = current ? FontWeights.Bold : FontWeights.SemiBold;
        }
    }
    /// <summary>超過這個長度就不著色；可編輯的 SQL 表面沿用同一條界線。</summary>
    /// <remarks>
    /// 著色要為每一個詞法單元建立一個 <see cref="Run"/>。幾千行的預存程序會產生
    /// 上萬個內嵌物件，版面計算的時間會讓人明顯感覺到卡頓，
    /// 而那種長度的定義本來就是拿去貼到別的地方看的。
    /// </remarks>
    public const int MaximumColorizedLength = 60_000;

    /// <summary>把指令碼排成一份可選取、可複製的流程文件。</summary>
    public static FlowDocument Build(string script, ResourceDictionary resources) =>
        Build(script, resources, null, out _);

    /// <summary>
    /// 同上，另外把幾段文字換底色，並交出每一處命中實際切出來的那幾個 <see cref="Run"/>。
    /// </summary>
    /// <param name="highlights">
    /// 要標出來的區段，索引落在 <paramref name="script"/> 上，且必須由小到大不重疊；
    /// 交出來的位置對不上就傳 null，<b>不要</b>塞一組猜的——畫錯位置的高亮看起來像比對錯了。
    /// </param>
    /// <param name="matches">
    /// 與 <paramref name="highlights"/> 同樣順序、同樣長度的一份清單，每一項是那一處命中
    /// 切出來的 <see cref="Run"/>。呼叫端用它把某一處換成「目前」的樣子並捲到可見。
    /// </param>
    /// <remarks>
    /// 一處命中可能跨好幾個 <see cref="Run"/>：高亮切的是原文位移，著色切的是詞法單元，
    /// 兩條界線不會對齊。交出去的是清單的清單而不是一個錨點，少了這一層的症狀是換成
    /// 「目前」的樣子時只有半個字變色。
    ///
    /// 高亮在<b>組文件的時候</b>就切進去，而不是事後對 <c>TextRange</c> 套屬性：套屬性會把
    /// <see cref="Run"/> 拆成新的物件，而原文位移是掛在 Run 上的（<see cref="SourceSpans"/>），
    /// 拆過之後複製選取會取到錯的一段原文。
    /// </remarks>
    public static FlowDocument Build(
        string script, ResourceDictionary resources, IReadOnlyList<MatchSpan>? highlights,
        out IReadOnlyList<IReadOnlyList<Run>> matches)
    {
        var writer = new Writer(highlights);
        var document = BuildCore(script, resources, writer);
        matches = writer.Matches;
        return document;
    }

    private static FlowDocument BuildCore(string script, ResourceDictionary resources, Writer writer)
    {
        var document = new FlowDocument
        {
            Resources = resources,
            PagePadding = new Thickness(8, 6, 8, 6),

            // 指令碼不換行：一行 CREATE TABLE 的欄位定義被折成兩行反而更難讀，
            // 讓水平捲軸負責就好。
            PageWidth = 4000
        };

        document.SetResourceReference(FlowDocument.FontFamilyProperty, ScriptResource.FontFamily);
        document.SetResourceReference(FlowDocument.FontSizeProperty, ScriptResource.FontSize);
        document.SetResourceReference(FlowDocument.ForegroundProperty, ScriptResource.Foreground);

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            TextAlignment = TextAlignment.Left
        };

        if (string.IsNullOrEmpty(script))
        {
            document.Blocks.Add(paragraph);
            return document;
        }

        if (script.Length > MaximumColorizedLength)
        {
            writer.Append(paragraph, script, ScriptResource.Foreground, 0);
            document.Blocks.Add(paragraph);
            return document;
        }

        var position = 0;

        foreach (var token in SqlTokenizer.TokenizeWithComments(script))
        {
            if (token.Start > position)
            {
                writer.Append(
                    paragraph, script.Substring(position, token.Start - position), ScriptResource.Foreground, position);
            }

            writer.Append(paragraph, token.Text, Classify(token), token.Start);
            position = token.End;
        }

        if (position < script.Length)
        {
            writer.Append(paragraph, script.Substring(position), ScriptResource.Foreground, position);
        }

        document.Blocks.Add(paragraph);
        return document;
    }

    /// <summary>詞法單元對應的著色分類；唯讀預覽與可編輯表面共用，兩邊的顏色不會分岔。</summary>
    public static ScriptResource Classify(SqlToken token)
    {
        return token.Kind switch
        {
            SqlTokenKind.Comment => ScriptResource.Comment,
            SqlTokenKind.String => ScriptResource.String,
            SqlTokenKind.Number => ScriptResource.Number,
            SqlTokenKind.Identifier => IdentifierBrush(token),
            _ => ScriptResource.Foreground
        };
    }

    /// <remarks>加了方括號的名稱一律不是關鍵字：<c>[KEY]</c> 是欄位名，不是 <c>KEY</c>。</remarks>
    private static ScriptResource IdentifierBrush(SqlToken token)
    {
        if (token.IsQuoted)
        {
            return ScriptResource.Foreground;
        }

        return SqlKeywordCatalog.IsKeywordOrDataType(token.Value)
            ? ScriptResource.Keyword
            : ScriptResource.Foreground;
    }

    /// <summary>
    /// 把文字寫成 <see cref="Inline"/>，並在需要時把命中那幾段切出來換底色。
    /// </summary>
    /// <remarks>
    /// 做成一個有狀態的寫入端，是因為「第一段高亮在哪個 Inline」要一路帶到最後；
    /// 靜態方法加 <c>ref</c> 參數穿過三層呼叫只會更難讀。
    /// </remarks>
    private sealed class Writer
    {
        private static readonly IReadOnlyList<Run>[] NoMatches = Array.Empty<IReadOnlyList<Run>>();

        private readonly IReadOnlyList<MatchSpan>? _highlights;
        private readonly List<Run>?[] _matches;

        internal Writer(IReadOnlyList<MatchSpan>? highlights)
        {
            _highlights = highlights;
            _matches = highlights is null || highlights.Count == 0
                ? Array.Empty<List<Run>?>()
                : new List<Run>?[highlights.Count];
        }

        /// <summary>每一處命中切出來的 Run，順序與傳進來的區段相同；沒有高亮時是空的。</summary>
        /// <remarks>
        /// 落在文字範圍外的區段一個 Run 都切不出來，那代表兩邊對不起來。這種時候那一項補成
        /// 空清單而不是整份少一項：索引要與呼叫端手上那份區段對得起來，少一項的症狀是
        /// 「第 5 處」從此指到第 6 段文字。
        /// </remarks>
        internal IReadOnlyList<IReadOnlyList<Run>> Matches
        {
            get
            {
                if (_matches.Length == 0) return NoMatches;

                var all = new IReadOnlyList<Run>[_matches.Length];
                for (var index = 0; index < _matches.Length; index++)
                {
                    all[index] = (IReadOnlyList<Run>?)_matches[index] ?? Array.Empty<Run>();
                }

                return all;
            }
        }

        /// <summary>
        /// 加入一段文字，換行改用 <see cref="LineBreak"/>。
        /// </summary>
        /// <remarks>
        /// <see cref="Run"/> 裡的換行字元不會斷行，整份指令碼會被排成一長行。
        /// </remarks>
        internal void Append(Paragraph paragraph, string text, ScriptResource brush, int sourceStart)
        {
            var start = 0;

            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] != '\n')
                {
                    continue;
                }

                var length = index - start;

                // 一併吃掉 \r\n 的 \r，否則會多出一個看不見的字元。
                if (length > 0 && text[index - 1] == '\r')
                {
                    length--;
                }

                if (length > 0)
                {
                    AppendRun(paragraph, text.Substring(start, length), brush, sourceStart + start);
                }

                var lineBreak = new LineBreak();
                SourceSpans.Add(lineBreak, new SourceSpan(sourceStart + start + length, index - start - length + 1));
                paragraph.Inlines.Add(lineBreak);
                start = index + 1;
            }

            if (start < text.Length)
            {
                AppendRun(paragraph, text.Substring(start), brush, sourceStart + start);
            }
        }

        /// <remarks>
        /// 高亮可能只蓋住一個詞法單元的一部分（搜 <c>Copy</c> 命中 <c>CopyNo</c>），
        /// 所以是在這一層依原文位移切，而不是整個 Run 換底色：整個換的話，使用者看到的
        /// 是「整個識別字都對上了」，而那不是他打的字。
        /// </remarks>
        private void AppendRun(Paragraph paragraph, string text, ScriptResource brush, int sourceStart)
        {
            if (_highlights is null || _highlights.Count == 0)
            {
                Emit(paragraph, text, brush, sourceStart, match: -1);
                return;
            }

            var at = 0;

            for (var index = 0; index < _highlights.Count; index++)
            {
                var span = _highlights[index];
                var start = Math.Max(span.Start - sourceStart, at);
                var end = Math.Min(span.End - sourceStart, text.Length);
                if (end <= start) continue;

                if (start > at)
                {
                    Emit(paragraph, text.Substring(at, start - at), brush, sourceStart + at, match: -1);
                }

                Emit(paragraph, text.Substring(start, end - start), brush, sourceStart + start, match: index);
                at = end;
            }

            if (at < text.Length)
            {
                Emit(paragraph, text.Substring(at), brush, sourceStart + at, match: -1);
            }
        }

        /// <param name="match">這一段屬於第幾處命中；不是命中時是 -1。</param>
        private void Emit(Paragraph paragraph, string text, ScriptResource brush, int sourceStart, int match)
        {
            var run = new Run(text);
            SourceSpans.Add(run, new SourceSpan(sourceStart, text.Length));
            // Run 只記住分類，不保存 Brush；切換主題不改變文字、選取與捲動位置。
            run.SetResourceReference(TextElement.ForegroundProperty, brush);

            if (match >= 0)
            {
                run.SetResourceReference(TextElement.BackgroundProperty, ScriptResource.Highlight);
                run.SetResourceReference(TextElement.ForegroundProperty, ScriptResource.HighlightForeground);
                // 字重是第二個維度：顏色之外還有一級，而目前那一處再加一級。
                run.FontWeight = FontWeights.SemiBold;
                (_matches[match] ??= new List<Run>()).Add(run);
            }

            paragraph.Inlines.Add(run);
        }
    }

    /// <summary>選取映射回原文，避免 TextRange.Text 將 LF 轉成 CRLF 或夾入段落結尾。</summary>
    public static string ReadOriginalSelection(RichTextBox viewer, string original)
    {
        var start = OriginalOffset(viewer.Document, viewer.Selection.Start, original.Length);
        var end = OriginalOffset(viewer.Document, viewer.Selection.End, original.Length);
        return original.Substring(start, Math.Max(0, end - start));
    }

    private static int OriginalOffset(FlowDocument document, TextPointer pointer, int length)
    {
        if (document.Blocks.FirstBlock is not Paragraph paragraph) return 0;
        foreach (var inline in paragraph.Inlines)
        {
            if (!SourceSpans.TryGetValue(inline, out var span)) continue;
            if (pointer.CompareTo(inline.ContentStart) <= 0) return span.Start;
            if (pointer.CompareTo(inline.ContentEnd) <= 0)
                return inline is Run ? span.Start + Math.Min(span.Length, inline.ContentStart.GetOffsetToPosition(pointer)) : span.Start;
            if (pointer.CompareTo(inline.ElementEnd) <= 0) return span.Start + span.Length;
        }
        return length;
    }
}
