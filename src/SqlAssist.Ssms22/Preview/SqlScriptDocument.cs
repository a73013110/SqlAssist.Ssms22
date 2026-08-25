using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text.Classification;
using SqlAssist.Core;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Ssms22.Preview;

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
/// <see cref="IClassificationFormatMap"/> 借，看起來仍然是 SSMS 目前的佈景主題。
/// </remarks>
internal static class SqlScriptDocument
{
    /// <summary>超過這個長度就不著色。</summary>
    /// <remarks>
    /// 著色要為每一個詞法單元建立一個 <see cref="Run"/>。幾千行的預存程序會產生
    /// 上萬個內嵌物件，版面計算的時間會讓人明顯感覺到卡頓，
    /// 而那種長度的定義本來就是拿去貼到別的地方看的。
    /// </remarks>
    private const int MaximumColorizedLength = 60_000;

    /// <summary>字型與顏色都取不到時的備援，與 SSMS 查詢視窗的預設值一致。</summary>
    private static readonly FontFamily FallbackFont = new("Consolas");

    private const double FallbackFontSize = 12.5;

    public sealed class Palette
    {
        public Palette(
            FontFamily fontFamily,
            double fontSize,
            Brush foreground,
            Brush keyword,
            Brush comment,
            Brush text,
            Brush number)
        {
            FontFamily = fontFamily;
            FontSize = fontSize;
            Foreground = foreground;
            Keyword = keyword;
            Comment = comment;
            Text = text;
            Number = number;
        }

        public FontFamily FontFamily { get; }

        public double FontSize { get; }

        public Brush Foreground { get; }

        public Brush Keyword { get; }

        public Brush Comment { get; }

        public Brush Text { get; }

        public Brush Number { get; }
    }

    /// <summary>
    /// 讀出編輯器目前的字型與分類顏色。
    /// </summary>
    /// <remarks>
    /// 直接寫死顏色的話，深色佈景主題下會變成看不見的字。
    /// 任何一項取不到就退回可讀的預設值，不讓整個分頁因為配色而失效。
    /// </remarks>
    public static Palette CreatePalette()
    {
        var fontFamily = FallbackFont;
        var fontSize = FallbackFontSize;
        var foreground = VsThemeBrushes.ListForeground;
        var keyword = foreground;
        var comment = VsThemeBrushes.DimForeground;
        var text = foreground;
        var number = foreground;

        try
        {
            if (SqlPreviewServices.Current is { } services &&
                services.TryGetTextFormatMap() is { } formatMap)
            {
                var defaults = formatMap.DefaultTextProperties;

                if (!defaults.TypefaceEmpty)
                {
                    fontFamily = defaults.Typeface.FontFamily;
                }

                if (!defaults.FontRenderingEmSizeEmpty)
                {
                    fontSize = defaults.FontRenderingEmSize;
                }

                if (!defaults.ForegroundBrushEmpty)
                {
                    foreground = defaults.ForegroundBrush;
                }

                var registry = services.ClassificationRegistry;
                keyword = Resolve(formatMap, registry, PredefinedClassificationTypeNames.Keyword, foreground);
                comment = Resolve(formatMap, registry, PredefinedClassificationTypeNames.Comment, comment);
                text = Resolve(formatMap, registry, PredefinedClassificationTypeNames.String, foreground);
                number = Resolve(formatMap, registry, PredefinedClassificationTypeNames.Number, foreground);
            }
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"解析指令碼配色失敗：{exception.Message}");
        }

        return new Palette(fontFamily, fontSize, foreground, keyword, comment, text, number);
    }

    private static Brush Resolve(
        IClassificationFormatMap formatMap,
        IClassificationTypeRegistryService registry,
        string classificationName,
        Brush fallback)
    {
        var classification = registry.GetClassificationType(classificationName);

        if (classification is null)
        {
            return fallback;
        }

        var properties = formatMap.GetTextProperties(classification);
        return properties.ForegroundBrushEmpty ? fallback : properties.ForegroundBrush;
    }

    /// <summary>把指令碼排成一份可選取、可複製的流程文件。</summary>
    public static FlowDocument Build(string script, Palette palette)
    {
        var document = new FlowDocument
        {
            FontFamily = palette.FontFamily,
            FontSize = palette.FontSize,
            Foreground = palette.Foreground,
            PagePadding = new Thickness(8, 6, 8, 6),

            // 指令碼不換行：一行 CREATE TABLE 的欄位定義被折成兩行反而更難讀，
            // 讓水平捲軸負責就好。
            PageWidth = 4000
        };

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
            Append(paragraph, script, palette.Foreground);
            document.Blocks.Add(paragraph);
            return document;
        }

        var position = 0;

        foreach (var token in Tokenize(script))
        {
            if (token.Start > position)
            {
                Append(paragraph, script.Substring(position, token.Start - position), palette.Foreground);
            }

            Append(paragraph, token.Text, BrushFor(token, palette));
            position = token.End;
        }

        if (position < script.Length)
        {
            Append(paragraph, script.Substring(position), palette.Foreground);
        }

        document.Blocks.Add(paragraph);
        return document;
    }

    private static IReadOnlyList<SqlToken> Tokenize(string script)
    {
        try
        {
            return SqlTokenizer.TokenizeWithComments(script);
        }
        catch (Exception exception)
        {
            // 著色失敗只該讓指令碼變成黑白，不該讓分頁開不起來。
            SqlAssistDiagnostics.WriteAlways($"指令碼著色分析失敗：{exception.Message}");
            return Array.Empty<SqlToken>();
        }
    }

    private static Brush BrushFor(SqlToken token, Palette palette)
    {
        return token.Kind switch
        {
            SqlTokenKind.Comment => palette.Comment,
            SqlTokenKind.String => palette.Text,
            SqlTokenKind.Number => palette.Number,
            SqlTokenKind.Identifier => IdentifierBrush(token, palette),
            _ => palette.Foreground
        };
    }

    /// <remarks>加了方括號的名稱一律不是關鍵字：<c>[KEY]</c> 是欄位名，不是 <c>KEY</c>。</remarks>
    private static Brush IdentifierBrush(SqlToken token, Palette palette)
    {
        if (token.IsQuoted)
        {
            return palette.Foreground;
        }

        return SqlKeywordCatalog.IsKeywordOrDataType(token.Value)
            ? palette.Keyword
            : palette.Foreground;
    }

    /// <summary>
    /// 加入一段文字，換行改用 <see cref="LineBreak"/>。
    /// </summary>
    /// <remarks>
    /// <see cref="Run"/> 裡的換行字元不會斷行，整份指令碼會被排成一長行。
    /// </remarks>
    private static void Append(Paragraph paragraph, string text, Brush brush)
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
                paragraph.Inlines.Add(new Run(text.Substring(start, length)) { Foreground = brush });
            }

            paragraph.Inlines.Add(new LineBreak());
            start = index + 1;
        }

        if (start < text.Length)
        {
            paragraph.Inlines.Add(new Run(text.Substring(start)) { Foreground = brush });
        }
    }
}
