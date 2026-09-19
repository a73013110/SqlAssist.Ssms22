using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SqlAssist.Core.Search;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// SQL Search 工具窗的共用樣板。
/// </summary>
/// <remarks>
/// 與 SQL Memory 的卡片共用容器樣式與進場動畫，只有列的內容不同：搜尋結果沒有時間、
/// 沒有狀態膠囊，卻有高亮區段。放在這裡而不是 <c>Search/</c>，理由與其他功能一樣——
/// 樣式只有 <see cref="SqlAssistChrome"/> 一個來源，呼叫端只組版面。
/// </remarks>
internal static partial class SqlAssistChrome
{
    /// <summary>
    /// 結果列：限定名稱一行、路徑或定義片段一行。
    /// </summary>
    /// <remarks>
    /// 只讀 <c>SearchHit</c> 攤出來的欄位（標題、路徑、片段、高亮區段、分類），
    /// 不認識任何一種酬載。加一個 provider 時這份樣板一個字都不必改。
    /// </remarks>
    public static DataTemplate CreateSearchHitTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));

        var heading = new FrameworkElementFactory(typeof(DockPanel));
        panel.AppendChild(heading);

        var category = new FrameworkElementFactory(typeof(TextBlock)) { Name = "category" };
        category.SetValue(DockPanel.DockProperty, Dock.Right);
        category.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        category.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        category.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        category.SetValue(FrameworkElement.MaxWidthProperty, 132d);
        category.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        category.SetBinding(TextBlock.TextProperty, new Binding("CategoryLabel"));
        heading.AppendChild(category);

        var title = new FrameworkElementFactory(typeof(SqlHighlightText));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetBinding(SqlHighlightText.SourceTextProperty, new Binding("Title"));
        title.SetBinding(SqlHighlightText.SpansProperty, new Binding("TitleSpans"));
        title.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Title"));
        heading.AppendChild(title);

        // 名稱命中的第二行是限定路徑；本文命中的第二行是那一行定義，兩者不會同時出現。
        var path = new FrameworkElementFactory(typeof(TextBlock)) { Name = "path" };
        path.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        path.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        path.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        path.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
        path.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        path.SetBinding(TextBlock.TextProperty, new Binding("Path"));
        path.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Path"));
        panel.AppendChild(path);

        var code = new FrameworkElementFactory(typeof(Border)) { Name = "code" };
        code.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        code.SetValue(Border.PaddingProperty, new Thickness(6, 2, 6, 2));
        code.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 1));
        code.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        code.SetResourceReference(Border.BackgroundProperty, ThemeBrush.RowAlternate);

        var snippet = new FrameworkElementFactory(typeof(SqlHighlightText));
        snippet.SetValue(TextBlock.FontFamilyProperty, CodeFont);
        snippet.SetValue(TextBlock.LineHeightProperty, 16d);
        snippet.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
        snippet.SetValue(FrameworkElement.MaxHeightProperty, 16d);
        snippet.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        snippet.SetBinding(SqlHighlightText.SourceTextProperty, new Binding("Snippet"));
        snippet.SetBinding(SqlHighlightText.SpansProperty, new Binding("SnippetSpans"));
        snippet.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Snippet"));
        code.AppendChild(snippet);
        panel.AppendChild(code);

        var template = new DataTemplate { VisualTree = panel };

        var body = new DataTrigger { Binding = new Binding("MatchTarget"), Value = SearchMatchTarget.Text };
        body.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "code"));
        body.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "path"));
        template.Triggers.Add(body);

        // 沒有路徑概念的來源（片段、設定）不留一條空白列。
        var noPath = new DataTrigger { Binding = new Binding("Path"), Value = "" };
        noPath.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "path"));
        template.Triggers.Add(noPath);

        // 停駐與選取時分類文字跟著卡片的配對前景，否則深色選取底上那一行會掉到讀不出來。
        foreach (var property in new[] { "IsSelected", "IsMouseOver" })
        {
            var selected = new DataTrigger
            {
                Binding = new Binding(property)
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
                },
                Value = true
            };
            selected.Setters.Add(ThemeResourceSet.Setter(TextBlock.ForegroundProperty, ThemeBrush.SelectedForeground, "category"));
            selected.Setters.Add(ThemeResourceSet.Setter(TextBlock.ForegroundProperty, ThemeBrush.SelectedForeground, "path"));
            template.Triggers.Add(selected);
        }

        return template;
    }

    /// <summary>
    /// 名稱命中與本文命中之間的分組標頭。
    /// </summary>
    /// <remarks>
    /// 沿用單行脈絡文字的規格（淡色、Caption），不另做一條看起來像第二條標題列的粗體區塊。
    /// <see cref="GroupStyle.Panel"/> 指定虛擬化面板：不指定的話分組容器退回 StackPanel，
    /// 一次把整組結果都具體化，而這份清單一輪可以有兩百列。
    /// </remarks>
    public static GroupStyle CreateSearchGroupStyle()
    {
        var header = new FrameworkElementFactory(typeof(DockPanel));
        header.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 8, 2, 2));

        var count = new FrameworkElementFactory(typeof(TextBlock));
        count.SetValue(DockPanel.DockProperty, Dock.Right);
        count.SetValue(TextBlock.FontFamilyProperty, InterfaceFont);
        count.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        count.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        count.SetBinding(TextBlock.TextProperty, new Binding("ItemCount"));
        header.AppendChild(count);

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetValue(TextBlock.FontFamilyProperty, InterfaceFont);
        name.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        header.AppendChild(name);

        var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));

        return new GroupStyle
        {
            HeaderTemplate = new DataTemplate { VisualTree = header },
            Panel = new ItemsPanelTemplate(panel)
        };
    }

    /// <summary>工具列上的搜尋選項；與對話框的核取方塊同一個外觀，只是排成一列。</summary>
    public static CheckBox CreateSearchOption(string label, string toolTip)
    {
        var box = new CheckBox
        {
            Content = label,
            Template = CreateCheckBoxTemplate(),
            FontFamily = InterfaceFont,
            FontSize = DefaultMetrics.Caption,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 10, 2),
            ToolTip = toolTip
        };
        box.SetResourceReference(Control.ForegroundProperty, ThemeBrush.ListForeground);
        AutomationProperties.SetName(box, label);
        return box;
    }

    /// <summary>狀態回饋的單次縮放長度；狀態回饋這一級的上限是 400 ms。</summary>
    public static readonly System.TimeSpan SearchStatusPop = System.TimeSpan.FromMilliseconds(240);


    /// <summary>
    /// 狀態換了一種說法時的一次縮放回饋。
    /// </summary>
    /// <remarks>
    /// 走 <see cref="UIElement.RenderTransform"/>，不改版面尺寸：狀態列在頁尾，改尺寸會把主內容
    /// 推上推下。「同一狀態不重播」由呼叫端負責——這裡看不出兩次呼叫是不是同一件事。
    /// </remarks>
    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    public static void PlayStatusPop(FrameworkElement element, bool? motion = null)
    {
        if (element.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            element.RenderTransform = scale;
            element.RenderTransformOrigin = new Point(0, 0.5);
        }

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!(motion ?? MotionEnabled)) return;

        var pop = new DoubleAnimationUsingKeyFrames { Duration = SearchStatusPop, FillBehavior = FillBehavior.Stop };
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(0.94, KeyTime.FromPercent(0)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseOut }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>清單的空狀態：置中的單行說明，與載入圖示疊在同一塊內容上，不另開一個表面。</summary>
    public static TextBlock CreateSearchEmptyState()
    {
        var text = CreateHint("", DefaultMetrics);
        text.TextAlignment = TextAlignment.Center;
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Margin = new Thickness(24, 0, 24, 0);
        text.MaxWidth = 320;
        text.IsHitTestVisible = false;
        return text;
    }
}
