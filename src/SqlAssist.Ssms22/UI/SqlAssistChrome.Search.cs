using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SqlAssist.Core.Search;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// SQL Search 工具窗的共用樣板。
/// </summary>
/// <remarks>
/// 與 SQL Memory 的卡片共用容器樣式與進場動畫，只有列的內容不同：搜尋結果沒有時間、
/// 沒有狀態膠囊，卻有高亮區段與命中部位。放在這裡而不是 <c>Search/</c>，理由與其他功能一樣——
/// 樣式只有 <see cref="SqlAssistChrome"/> 一個來源，呼叫端只組版面。
/// </remarks>
internal static partial class SqlAssistChrome
{
    /// <summary>
    /// 結果列：圖示、名稱與命中部位一行，限定名稱與脈絡膠囊一行，本文命中再加一行片段。
    /// </summary>
    /// <remarks>
    /// 只讀 <c>SearchHit</c> 攤出來的欄位（標題、分類 Id、路徑、片段、高亮區段、膠囊、命中部位），
    /// <b>不認識任何一種酬載</b>。加一個 provider 時這份樣板一個字都不必改；向下轉型
    /// <c>ActivatePayload</c> 來挑圖示的那一刻起，清單就只畫得出目錄物件。
    ///
    /// 取消了原本的上下分組：分組把同一批結果切成兩疊，要找的那一筆可能在第二疊的底下。
    /// 每一列掛一顆命中部位徽章一樣分得出來，而排序可以換成使用者真正要的那一種。
    /// </remarks>
    public static DataTemplate CreateSearchHitTemplate()
    {
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetBinding(AutomationProperties.NameProperty, new Binding("Description"));

        // 圖示佔左欄並貼齊首行；第二、三行縮排在它右邊，一列讀下來只有一條左緣軸線。
        var kind = new FrameworkElementFactory(typeof(Border)) { Name = "kind" };
        kind.SetValue(DockPanel.DockProperty, Dock.Left);
        kind.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        kind.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        kind.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
        // 形狀之外還要讀得到種類文字：列上已經沒有那幾個字，Tooltip 是它唯一的去處。
        kind.SetBinding(FrameworkElement.ToolTipProperty, new Binding("CategoryLabel"));
        var glyph = new FrameworkElementFactory(typeof(SqlIconImage));
        glyph.SetBinding(SqlIconImage.CategoryIdProperty, new Binding("CategoryId"));
        kind.AppendChild(glyph);
        root.AppendChild(kind);

        var lines = new FrameworkElementFactory(typeof(StackPanel));
        root.AppendChild(lines);

        var heading = new FrameworkElementFactory(typeof(DockPanel));
        lines.AppendChild(heading);

        var actions = new FrameworkElementFactory(typeof(StackPanel)) { Name = "actions" };
        actions.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        actions.SetValue(DockPanel.DockProperty, Dock.Right);
        actions.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
        // Hidden 保留尺寸：Collapsed 會讓列在停駐的瞬間重新排版，而互動狀態不得改變版面尺寸。
        actions.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
        foreach (var command in SqlSearchRowCommand.All)
        {
            actions.AppendChild(CreateRowActionButton(
                "action" + command.Action, command.Action, command.Icon, command.Label,
                SqlActionTone.Neutral, separated: false));
        }
        heading.AppendChild(actions);

        var target = BoundTextBadge("TargetLabel", "target");
        target.SetValue(DockPanel.DockProperty, Dock.Right);
        target.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        heading.AppendChild(target);

        var title = new FrameworkElementFactory(typeof(SqlHighlightText));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        title.SetBinding(SqlHighlightText.SourceTextProperty, new Binding("Title"));
        title.SetBinding(SqlHighlightText.SpansProperty, new Binding("TitleSpans"));
        title.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Title"));
        heading.AppendChild(title);

        var context = new FrameworkElementFactory(typeof(DockPanel)) { Name = "context" };
        context.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
        lines.AppendChild(context);

        // 膠囊靠右並固定在它自己的寬度上；限定名稱吃剩下的空間，窄窗先省略的是名稱中段。
        var badges = new FrameworkElementFactory(typeof(ItemsControl)) { Name = "badges" };
        badges.SetValue(DockPanel.DockProperty, Dock.Right);
        badges.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        var badgePanel = new FrameworkElementFactory(typeof(StackPanel));
        badgePanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        badges.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(badgePanel));
        badges.SetValue(ItemsControl.ItemTemplateProperty, CreateSearchBadgeTemplate());
        badges.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Badges"));
        context.AppendChild(badges);

        var path = new FrameworkElementFactory(typeof(TextBlock)) { Name = "path" };
        path.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        path.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        path.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        path.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        path.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        path.SetBinding(TextBlock.TextProperty, new Binding("Path"));
        path.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Path"));
        context.AppendChild(path);

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
        lines.AppendChild(code);

        var template = new DataTemplate { VisualTree = root };

        // 本文命中才多一行片段；名稱與資料行命中的片段就是名稱本體，再畫一次是同一句話說兩遍。
        var body = new DataTrigger { Binding = new Binding("MatchTarget"), Value = SearchMatchTarget.Text };
        body.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "code"));
        template.Triggers.Add(body);

        // 沒有路徑概念的來源（片段、設定）不留一條空白列；膠囊仍留在原處。
        var noPath = new DataTrigger { Binding = new Binding("Path"), Value = "" };
        noPath.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "path"));
        template.Triggers.Add(noPath);

        // 停駐與選取時淡色那一行跟著卡片的配對前景，否則深色選取底上那一行會掉到讀不出來。
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
            selected.Setters.Add(ThemeResourceSet.Setter(TextBlock.ForegroundProperty, ThemeBrush.SelectedForeground, "path"));
            template.Triggers.Add(selected);
        }

        // 鍵盤走到這一列也揭露動作；只鍵盤操作的人不該看不到它們。
        foreach (var property in new[] { "IsMouseOver", "IsKeyboardFocusWithin" })
        {
            var hover = new DataTrigger
            {
                Binding = new Binding(property)
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
                },
                Value = true
            };
            hover.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "actions"));
            template.Triggers.Add(hover);
        }

        return template;
    }

    /// <summary>結果列右下角的脈絡膠囊；資料來自 <c>SearchHit.Badges</c>，與 SQL Memory 的連線膠囊同一份外觀。</summary>
    public static DataTemplate CreateSearchBadgeTemplate() =>
        new() { VisualTree = BoundBadge("Text", "badge", iconProperty: "Icon") };

    /// <summary>
    /// 分段開關裡的一段。
    /// </summary>
    /// <remarks>
    /// 與分頁的分段控制器同一個外觀，差別只在它是<b>可複選</b>的切換鈕：比對位置是旗標，
    /// 三段可以同時亮。因此選取狀態讀 <see cref="ToggleButton.IsCheckedProperty"/> 而不是
    /// <c>IsSelected</c>，其餘的底槽、圓角與「滑鼠掃過只提亮字」都相同。
    /// </remarks>
    public static Style CreateSegmentToggleStyle()
    {
        var segment = new FrameworkElementFactory(typeof(Border)) { Name = "segment" };
        segment.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        segment.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        segment.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        segment.SetValue(Border.CornerRadiusProperty, new CornerRadius(InnerRadius));
        segment.SetBinding(Border.PaddingProperty, TemplatedParent(nameof(Control.Padding)));

        var label = new FrameworkElementFactory(typeof(ContentPresenter)) { Name = "label" };
        label.SetResourceReference(TextElement.ForegroundProperty, ThemeBrush.DimForeground);
        label.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        segment.AppendChild(label);

        var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = segment };

        AddTrigger(template, UIElement.IsMouseOverProperty, TextElement.ForegroundProperty, ThemeBrush.ListForeground, "label");
        AddTrigger(template, UIElement.IsMouseOverProperty, Control.ForegroundProperty, ThemeBrush.ListForeground);

        // 選取寫在滑鼠之後：兩個條件同時成立時，後宣告的那一個才是使用者要看的。
        var selected = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        selected.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, ThemeBrush.ListBackground, "segment"));
        selected.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.Hairline, "segment"));
        selected.Setters.Add(ThemeResourceSet.Setter(TextElement.ForegroundProperty, ThemeBrush.ListForeground, "label"));
        selected.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.ListForeground));
        template.Triggers.Add(selected);

        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty, Border.BorderBrushProperty, ThemeBrush.AccentBorder, "segment");

        var style = new Style(typeof(ToggleButton));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 3, 10, 4)));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, InterfaceFont));
        style.Setters.Add(new Setter(Control.FontSizeProperty, DefaultMetrics.Caption));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 22d));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.DimForeground));
        return style;
    }

    /// <summary>工具列上的過濾下拉按鈕；與其他工具列按鈕同高，不另立一種外觀。</summary>
    public static Style CreateFilterButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, InterfaceFont));
        style.Setters.Add(new Setter(Control.FontSizeProperty, DefaultMetrics.Body));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 26d));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.ListForeground));
        return style;
    }

    /// <summary>
    /// 已選條件列上的一顆 chip：中性膠囊加一個清除鈕。
    /// </summary>
    /// <remarks>
    /// 用中性色而不是強調色：chip 說的是「現在有這個條件」，不是警示，也不是一種分類。
    /// 清除鈕是幽靈按鈕，停駐才顯色——它與 chip 本身是同一顆可按的東西，畫兩個邊框只會多一圈線。
    /// </remarks>
    public static Border CreateFilterChip(string text, out Button remove)
    {
        var content = new DockPanel { VerticalAlignment = VerticalAlignment.Center };

        remove = CreateButton("", DefaultMetrics);
        remove.Content = CreateIcon(SqlIcon.Clear);
        remove.Template = CreateGhostButtonTemplate();
        remove.Padding = new Thickness(1);
        remove.Margin = new Thickness(4, 0, 0, 0);
        remove.MinWidth = 18;
        remove.MinHeight = 18;
        remove.ToolTip = "清除條件：" + text;
        AutomationProperties.SetName(remove, "清除條件：" + text);
        DockPanel.SetDock(remove, Dock.Right);
        content.Children.Add(remove);

        var label = new TextBlock
        {
            Text = text,
            FontFamily = InterfaceFont,
            FontSize = DefaultMetrics.Caption,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = text
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        content.Children.Add(label);

        return new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 1, 4, 1),
            Margin = new Thickness(0, 0, 4, 0),
            MaxWidth = 220,
            VerticalAlignment = VerticalAlignment.Center
        }.WithTheme(Border.BackgroundProperty, ThemeBrush.BadgeBackground)
            .WithTheme(Border.BorderBrushProperty, ThemeBrush.Hairline);
    }

    /// <summary>
    /// 過濾面板的選項清單：recycling 虛擬化的 <see cref="ItemsControl"/>，捲軸沿用覆蓋式。
    /// </summary>
    /// <remarks>
    /// 不用 <c>ScrollViewer</c> 疊 <c>StackPanel</c>：那個形狀在面板展開的那一刻，就把每一個
    /// 資料庫、每一個分類都建成一顆 <see cref="CheckBox"/>，而面板一次只看得到十來列。
    /// 樣板在這裡建一次給所有列共用，回收的容器換的只有 <c>DataContext</c>；快取的是
    /// <see cref="ControlTemplate"/> 與 <see cref="DataTemplate"/>，不是已經有 parent 的元素。
    ///
    /// 標題與選項攤成同一份平的清單，不做巢狀分組：分組要另外開
    /// <c>IsVirtualizingWhenGrouping</c> 才虛擬化得了，而那是一個很容易漏掉的開關。
    /// </remarks>
    /// <param name="maxHeight">面板限高；捲的是選項本身，搜尋框與命令鈕要一直看得見。</param>
    public static ItemsControl CreateSearchOptionList(double maxHeight)
    {
        var rows = new SearchOptionRowSelector(CreateSearchCaptionRow(), CreateSearchOptionRow(CreateCheckBoxTemplate()));
        var list = new ItemsControl
        {
            MaxHeight = maxHeight,
            Focusable = false,
            ItemTemplateSelector = rows,
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
            Template = CreateSearchOptionListTemplate()
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        return list;
    }

    /// <summary>清單殼層：覆蓋式捲軸加 <see cref="ItemsPresenter"/>。</summary>
    /// <remarks>
    /// <c>CanContentScroll</c> 設在這裡而不是外面：它不是可繼承的屬性，虛擬化面板要當上
    /// <c>IScrollInfo</c> 就得由這一層的 <see cref="ScrollViewer"/> 自己開。
    /// </remarks>
    private static ControlTemplate CreateSearchOptionListTemplate()
    {
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(UIElement.FocusableProperty, false);
        scroll.SetValue(Control.TemplateProperty, CreateOverlayScrollTemplate());
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        return new ControlTemplate(typeof(ItemsControl)) { VisualTree = scroll };
    }

    /// <summary>段落標題列；與 <see cref="CreateLabel"/> 同一種字重與色階，上緣間距由列自己帶。</summary>
    private static DataTemplate CreateSearchCaptionRow()
    {
        var caption = new FrameworkElementFactory(typeof(TextBlock));
        caption.SetBinding(TextBlock.TextProperty, new Binding(nameof(SqlSearchFilterRow.Label)));
        caption.SetBinding(FrameworkElement.MarginProperty, new Binding(nameof(SqlSearchFilterRow.Margin)));
        caption.SetValue(TextBlock.FontFamilyProperty, InterfaceFont);
        caption.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        caption.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        caption.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        var template = new DataTemplate(typeof(SqlSearchFilterRow)) { VisualTree = caption };
        template.Seal();
        return template;
    }

    /// <summary>選項列；與對話框的核取方塊同一個外觀，狀態由繫結帶。</summary>
    /// <remarks>
    /// <see cref="ToggleButton.IsCheckedProperty"/> 走雙向繫結而不是 <c>Checked</c>／<c>Unchecked</c>：
    /// 回收的容器換 DataContext 時繫結會把新值推進來，那不是使用者的動作，掛事件等於替他按一次。
    /// </remarks>
    private static DataTemplate CreateSearchOptionRow(ControlTemplate box)
    {
        var option = new FrameworkElementFactory(typeof(CheckBox));
        option.SetValue(Control.TemplateProperty, box);
        option.SetValue(Control.FontFamilyProperty, InterfaceFont);
        option.SetValue(Control.FontSizeProperty, DefaultMetrics.Caption);
        option.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        option.SetBinding(FrameworkElement.MarginProperty, new Binding(nameof(SqlSearchFilterRow.Margin)));
        option.SetBinding(ContentControl.ContentProperty, new Binding(nameof(SqlSearchFilterRow.Label)));
        option.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(SqlSearchFilterRow.ToolTip)));
        option.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(SqlSearchFilterRow.Label)));
        option.SetBinding(ToggleButton.IsCheckedProperty,
            new Binding(nameof(SqlSearchFilterRow.IsSelected)) { Mode = BindingMode.TwoWay });
        option.SetResourceReference(Control.ForegroundProperty, ThemeBrush.ListForeground);
        var template = new DataTemplate(typeof(SqlSearchFilterRow)) { VisualTree = option };
        template.Seal();
        return template;
    }

    /// <summary>兩種列共用一份平清單；回收的容器換 DataContext 時會重挑樣板。</summary>
    private sealed class SearchOptionRowSelector : DataTemplateSelector
    {
        private readonly DataTemplate _caption;
        private readonly DataTemplate _option;

        public SearchOptionRowSelector(DataTemplate caption, DataTemplate option)
        {
            _caption = caption;
            _option = option;
        }

        public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
            item is SqlSearchFilterRow { IsCaption: true } ? _caption : _option;
    }

    /// <summary>搜尋框裡的選項開關（大小寫、全字）；切換鈕沿用分段開關那一段的外觀。</summary>
    /// <remarks>
    /// 放在搜尋框裡而不是工具列上，是因為它們修飾的是<b>這個字串怎麼比</b>，不是搜哪裡；
    /// 而且工具列已經被真正的篩選佔滿，多兩顆就換不到一列。
    /// </remarks>
    public static ToggleButton CreateSearchToggle(SqlIcon icon, string label, string toolTip)
    {
        var toggle = new ToggleButton
        {
            Content = CreateIcon(icon),
            Style = CreateSegmentToggleStyle(),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 2, 0),
            MinWidth = 24,
            ToolTip = toolTip
        };
        AutomationProperties.SetName(toggle, label);
        return toggle;
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
