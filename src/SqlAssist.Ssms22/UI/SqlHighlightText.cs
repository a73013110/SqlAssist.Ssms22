using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using SqlAssist.Core.Matching;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 一行文字，命中的區段換底色；搜尋結果的名稱與片段共用。
/// </summary>
/// <remarks>
/// 做成控制項而不是在資料列上組好 <see cref="Inline"/> 交出來，是因為清單是 recycling 虛擬化：
/// 容器會被換給另一列，而 <see cref="Inline"/> 只能掛在一棵視覺樹上。以相依屬性繫結，
/// 換列時重建 Run，換主題時只換資源，兩件事都不必重建控制項。
///
/// 高亮只換底色與字重，不改字級與邊界：改了尺寸的話，同一列在命中與不命中之間會跳動。
/// </remarks>
internal sealed class SqlHighlightText : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText), typeof(string), typeof(SqlHighlightText),
        new PropertyMetadata("", OnContentChanged));

    public static readonly DependencyProperty SpansProperty = DependencyProperty.Register(
        nameof(Spans), typeof(IReadOnlyList<MatchSpan>), typeof(SqlHighlightText),
        new PropertyMetadata(null, OnContentChanged));

    public SqlHighlightText()
    {
        TextTrimming = TextTrimming.CharacterEllipsis;
        TextWrapping = TextWrapping.NoWrap;
    }

    /// <summary>要顯示的整行文字；<see cref="TextBlock.Text"/> 由這裡與 <see cref="Spans"/> 一起算出來。</summary>
    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    /// <summary>要高亮的區段，索引落在 <see cref="SourceText"/> 上；落在範圍外的整段忽略。</summary>
    public IReadOnlyList<MatchSpan>? Spans
    {
        get => (IReadOnlyList<MatchSpan>?)GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    private static void OnContentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((SqlHighlightText)sender).Rebuild();

    private void Rebuild()
    {
        var text = SourceText ?? "";
        var spans = Spans;

        Inlines.Clear();

        // 一律只動 Inlines，不設 Text：兩者是同一份內容的兩個入口，設了 Text 會把剛加進去的
        // Run 全部丟掉，而症狀是高亮時有時無。
        if (spans is null || spans.Count == 0)
        {
            if (text.Length != 0) Inlines.Add(new Run(text));
            return;
        }

        var at = 0;

        foreach (var span in spans)
        {
            if (span.Start < at || span.End > text.Length) continue;
            if (span.Start > at) Inlines.Add(new Run(text.Substring(at, span.Start - at)));

            var hit = new Run(text.Substring(span.Start, span.Length)) { FontWeight = FontWeights.SemiBold };
            hit.SetResourceReference(TextElement.BackgroundProperty, ThemeBrush.AccentBackground);
            Inlines.Add(hit);
            at = span.End;
        }

        if (at < text.Length) Inlines.Add(new Run(text.Substring(at)));
    }
}
