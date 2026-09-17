using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using SqlAssist.Core.Keywords;
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
    Number
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
    /// <summary>超過這個長度就不著色；可編輯的 SQL 表面沿用同一條界線。</summary>
    /// <remarks>
    /// 著色要為每一個詞法單元建立一個 <see cref="Run"/>。幾千行的預存程序會產生
    /// 上萬個內嵌物件，版面計算的時間會讓人明顯感覺到卡頓，
    /// 而那種長度的定義本來就是拿去貼到別的地方看的。
    /// </remarks>
    public const int MaximumColorizedLength = 60_000;

    /// <summary>把指令碼排成一份可選取、可複製的流程文件。</summary>
    public static FlowDocument Build(string script, ResourceDictionary resources)
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
            Append(paragraph, script, ScriptResource.Foreground, 0);
            document.Blocks.Add(paragraph);
            return document;
        }

        var position = 0;

        foreach (var token in SqlTokenizer.TokenizeWithComments(script))
        {
            if (token.Start > position)
            {
                Append(paragraph, script.Substring(position, token.Start - position), ScriptResource.Foreground, position);
            }

            Append(paragraph, token.Text, Classify(token), token.Start);
            position = token.End;
        }

        if (position < script.Length)
        {
            Append(paragraph, script.Substring(position), ScriptResource.Foreground, position);
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
    /// 加入一段文字，換行改用 <see cref="LineBreak"/>。
    /// </summary>
    /// <remarks>
    /// <see cref="Run"/> 裡的換行字元不會斷行，整份指令碼會被排成一長行。
    /// </remarks>
    private static void Append(Paragraph paragraph, string text, ScriptResource brush, int sourceStart)
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

    private static void AppendRun(Paragraph paragraph, string text, ScriptResource brush, int sourceStart)
    {
        var run = new Run(text);
        SourceSpans.Add(run, new SourceSpan(sourceStart, text.Length));
        // Run 只記住分類，不保存 Brush；切換主題不改變文字、選取與捲動位置。
        run.SetResourceReference(TextElement.ForegroundProperty, brush);
        paragraph.Inlines.Add(run);
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
