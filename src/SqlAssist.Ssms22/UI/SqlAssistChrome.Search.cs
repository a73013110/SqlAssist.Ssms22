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
    /// 結果列：名稱、物件類型、命中部位與連線一行，限定名稱與命中的資料行各一行，
    /// 本文命中再加一行片段。
    /// </summary>
    /// <remarks>
    /// 只讀 <c>SearchHit</c> 攤出來的欄位（標題、分類 Id、路徑、片段、高亮區段、膠囊、命中部位），
    /// <b>不認識任何一種酬載</b>。加一個 provider 時這份樣板一個字都不必改；向下轉型
    /// <c>ActivatePayload</c> 來挑圖示的那一刻起，清單就只畫得出目錄物件。
    ///
    /// 取消了原本的上下分組：分組把同一批結果切成兩疊，要找的那一筆可能在第二疊的底下。
    /// 每一列掛一顆命中部位徽章一樣分得出來，而排序可以換成使用者真正要的那一種。
    /// </remarks>
    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    public static DataTemplate CreateSearchHitTemplate(bool? motion = null)
    {
        var lines = new FrameworkElementFactory(typeof(StackPanel));
        lines.SetBinding(AutomationProperties.NameProperty, new Binding("Description"));

        // 第一列：物件名稱 → 物件類型 → 命中部位 → 彈性空白 → 伺服器 → 資料庫，操作浮在右緣。
        // 名稱固定最左，要掃的那一欄每一列才從同一個位置開始；圖示排在它前面就不是。
        // 疊層、宣告順序、操作層與窄版降級由 SqlRowHeading 擔保，這裡只填欄位。
        var row = BeginRowHeading(lines);
        var identity = row.Identity;

        // 命中次數排在最右：它是這一列裡最不必每次都讀的一個數字，而且只有併過的列才有。
        var count = CreateTextBadge("MatchCountLabel", "count");
        count.SetValue(DockPanel.DockProperty, Dock.Right);
        count.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 0, 0));
        identity.AppendChild(count);

        // 命中部位<b>可以有好幾顆</b>：同一個物件被名稱、資料行與定義本文一起命中時，
        // 那是使用者要從這一列讀到的事，而聚合器已經把它們併成一列了。
        var targets = new FrameworkElementFactory(typeof(ItemsControl)) { Name = "targets" };
        targets.SetValue(DockPanel.DockProperty, Dock.Right);
        targets.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 0, 0));
        targets.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var targetPanel = new FrameworkElementFactory(typeof(StackPanel));
        targetPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        targets.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(targetPanel));
        targets.SetValue(ItemsControl.ItemTemplateProperty, CreateSearchTargetTemplate());
        targets.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("TargetLabels"));
        identity.AppendChild(targets);

        // 物件類型是看得見的 icon＋文字膠囊。同一顆原生圖示會落在好幾種目錄物件上，而
        // 「這是資料表還是檢視」正是掃這一列時要回答的問題；只留 Tooltip 等於要停駐才讀得到。
        var kind = CreateBadge("CategoryLabel", "kind", categoryProperty: "CategoryId");
        kind.SetValue(DockPanel.DockProperty, Dock.Right);
        kind.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 0, 0));
        identity.AppendChild(kind);

        // 名稱最後才量，剩多少吃多少並 ellipsis；全文在 Tooltip 與 Preview。
        var title = new FrameworkElementFactory(typeof(SqlHighlightText)) { Name = "name" };
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        title.SetValue(FrameworkElement.MaxWidthProperty, RowNameMaxWidth);
        title.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        title.SetBinding(SqlHighlightText.SourceTextProperty, new Binding("Title"));
        title.SetBinding(SqlHighlightText.SpansProperty, new Binding("TitleSpans"));
        title.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Title"));
        identity.AppendChild(title);

        // 窄版：連線膠囊降成 icon-only，次要操作收進 overflow；名稱與物件類型一直看得見。
        var narrow = row.Narrow;
        foreach (var command in SqlSearchRowCommand.All)
        {
            var button = CreateRowActionButton(
                "action" + command.Action, command.Action, command.Icon, command.Label,
                SqlActionTone.Neutral, separated: false, availabilityPath: command.AvailabilityPath);
            if (command.LabelPath is { } labelPath)
                button.SetBinding(AutomationProperties.NameProperty, new Binding(labelPath));
            if (command.ToolTipPath is { } toolTipPath)
                button.SetBinding(FrameworkElement.ToolTipProperty, new Binding(toolTipPath));
            if (!command.IsPrimary)
                narrow.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, button.Name));
            row.Actions.AppendChild(button);
        }

        // 連線膠囊靠右並固定在自己的寬度上；與名稱之間的彈性空白由 DockPanel 留著。
        var badges = new FrameworkElementFactory(typeof(ItemsControl)) { Name = "badges" };
        badges.SetValue(DockPanel.DockProperty, Dock.Right);
        badges.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        badges.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var badgePanel = new FrameworkElementFactory(typeof(StackPanel));
        badgePanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        badges.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(badgePanel));
        badges.SetValue(ItemsControl.ItemTemplateProperty, CreateSearchBadgeTemplate());
        badges.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Badges"));
        row.Heading.AppendChild(badges);

        // 內容列只剩限定名稱；窄窗先省略的是名稱中段。
        var path = new FrameworkElementFactory(typeof(TextBlock)) { Name = "path" };
        path.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
        path.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        path.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        path.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        path.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        path.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        path.SetBinding(TextBlock.TextProperty, new Binding("Path"));
        path.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Path"));
        lines.AppendChild(path);

        // 命中的資料行自己一列：標題已經收回物件本身（一張表的三個資料行命中是同一列），
        // 「是哪幾行」的答案只剩這裡說得出來，而那正是使用者搜這個字串要找的東西。
        var columns = new FrameworkElementFactory(typeof(SqlHighlightText)) { Name = "columns" };
        columns.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
        columns.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        columns.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        columns.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        columns.SetBinding(SqlHighlightText.SourceTextProperty, new Binding("Columns"));
        columns.SetBinding(SqlHighlightText.SpansProperty, new Binding("ColumnSpans"));
        columns.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Columns"));
        lines.AppendChild(columns);

        var code = new FrameworkElementFactory(typeof(Border)) { Name = "code" };
        code.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        code.SetValue(Border.PaddingProperty, new Thickness(6, 2, 6, 2));
        code.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 1));
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

        // 多選模式才出現的勾選欄在整列左邊，跨過所有內容列；平常收起，版面與沒有多選時相同。
        var template = new DataTemplate { VisualTree = WrapWithRowCheck(lines, "Title") };

        // 有本文片段才多一行；名稱與資料行命中的片段就是名稱本體，再畫一次是同一句話說兩遍。
        // 條件讀的是片段本身而不是代表那一筆的命中部位：併過的一列可能由資料行命中當代表，
        // 而唯一有程式碼可看的是被併進來的那一筆本文命中。
        var body = new DataTrigger { Binding = new Binding("Snippet"), Value = "" };
        body.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "code"));
        template.Triggers.Add(body);

        // 沒有路徑概念的來源（片段、設定）不留一條空白列。
        var noPath = new DataTrigger { Binding = new Binding("Path"), Value = "" };
        noPath.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "path"));
        template.Triggers.Add(noPath);

        // 沒有資料行命中時整列收起，不留一條空白；只有一處命中時也不畫那顆次數膠囊。
        var noColumns = new DataTrigger { Binding = new Binding("Columns"), Value = "" };
        noColumns.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "columns"));
        template.Triggers.Add(noColumns);

        var single = new DataTrigger { Binding = new Binding("MatchCountLabel"), Value = "" };
        single.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "count"));
        template.Triggers.Add(single);

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
            selected.Setters.Add(ThemeResourceSet.Setter(TextBlock.ForegroundProperty, ThemeBrush.SelectedForeground, "columns"));
            template.Triggers.Add(selected);
        }

        // 掛上左半、補 overflow、疊上操作層，並接上底色鏡射與揭露；與 SQL Memory 的卡片同一份。
        row.Complete(template, motion);
        RevealRowCheck(template);

        return template;
    }

    /// <summary>
    /// 預覽那一列資訊：與結果列第一列<b>同一個順序</b>的膠囊，放在主從區的抬頭上。
    /// </summary>
    /// <remarks>
    /// 順序相同不是美感問題：使用者在清單上選一筆、眼睛移到資訊列，同一組事實卻換了位置的話，
    /// 等於每一次都要重讀一遍。限定名稱排在最後，因為它是這一列唯一比清單多出來的東西
    /// （清單上它在第二列）。
    ///
    /// 預覽內容裡<b>不</b>再放第二份同樣的字：兩條灰色的字各說一次種類與命中部位，
    /// 是「一個視窗只有一個抬頭」那條規矩的反例，而它也把第一列的位置讓給了沒有新資訊的東西。
    /// </remarks>
    public static DataTemplate CreateSearchMetadataTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var name = BoundText("Title"); name.Name = "name";
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        name.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        panel.AppendChild(name);

        panel.AppendChild(CreateBadge("CategoryLabel", "kind", categoryProperty: "CategoryId"));

        // 與結果列同一份樣板、同一個順序：兩邊各排各的話，使用者在清單上選一筆、
        // 眼睛移到這一列，同一組膠囊卻換了位置。
        var targets = new FrameworkElementFactory(typeof(ItemsControl)) { Name = "targets" };
        var targetPanel = new FrameworkElementFactory(typeof(StackPanel));
        targetPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        targets.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(targetPanel));
        targets.SetValue(ItemsControl.ItemTemplateProperty, CreateSearchTargetTemplate());
        targets.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        targets.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("TargetLabels"));
        panel.AppendChild(targets);

        var count = CreateTextBadge("MatchCountLabel", "count");
        panel.AppendChild(count);

        var badges = new FrameworkElementFactory(typeof(ItemsControl)) { Name = "badges" };
        var badgePanel = new FrameworkElementFactory(typeof(StackPanel));
        badgePanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        badges.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(badgePanel));
        badges.SetValue(ItemsControl.ItemTemplateProperty, CreateSearchBadgeTemplate());
        badges.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        badges.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Badges"));
        panel.AppendChild(badges);

        var path = BoundText("Path"); path.Name = "path";
        path.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        path.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
        path.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        panel.AppendChild(path);

        var template = new DataTemplate { VisualTree = panel };
        // 沒有路徑概念的來源不留一個空的插槽；缺值直接收起是清單列與資訊列同一條規矩。
        var noPath = new DataTrigger { Binding = new Binding("Path"), Value = "" };
        noPath.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "path"));
        template.Triggers.Add(noPath);

        var single = new DataTrigger { Binding = new Binding("MatchCountLabel"), Value = "" };
        single.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "count"));
        template.Triggers.Add(single);
        return template;
    }

    /// <summary>
    /// 命中部位那一顆膠囊；清單列與預覽資訊列共用，資料只是一個字串。
    /// </summary>
    /// <remarks>
    /// 綁的是項目自己（<c>.</c>）而不是某個屬性：一列可以有好幾顆，而它們是一份字串清單，
    /// 不是三個各自具名的欄位——做成三個欄位的那一版，加第四種命中部位要改樣板。
    /// </remarks>
    public static DataTemplate CreateSearchTargetTemplate() =>
        new() { VisualTree = CreateTextBadge(".", "target") };

    /// <summary>結果列的脈絡膠囊；資料來自 <c>SearchHit.Badges</c>，與 SQL Memory 的連線膠囊同一份外觀。</summary>
    /// <remarks>窄版一起降成 icon-only：降級條件讀的是列自己的寬度模式，膠囊在哪一層容器裡都一樣。</remarks>
    public static DataTemplate CreateSearchBadgeTemplate()
    {
        var template = new DataTemplate { VisualTree = CreateBadge("Text", "badge", iconProperty: "Icon") };
        var narrow = NarrowRowTrigger();
        IconOnlyInNarrow(narrow, "badge");
        template.Triggers.Add(narrow);
        return template;
    }

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

    /// <summary>
    /// 搜尋框裡的選項開關（大小寫、全字）。
    /// </summary>
    /// <remarks>
    /// 放在搜尋框裡而不是工具列上，是因為它們修飾的是<b>這個字串怎麼比</b>，不是搜哪裡；
    /// 而且工具列已經被真正的篩選佔滿，多兩顆就換不到一列。常駐可見就是它們的完整呈現，
    /// 所以「開著」必須在這一顆上看得出來，走 <see cref="CreateToggleStyle"/>。
    /// </remarks>
    public static ToggleButton CreateSearchToggle(SqlIcon icon, string label, string toolTip)
    {
        var toggle = new ToggleButton
        {
            Content = CreateIcon(icon),
            Style = CreateToggleStyle(),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 2, 0),
            MinWidth = 24,
            ToolTip = toolTip
        };
        AutomationProperties.SetName(toggle, label);
        return toggle;
    }

    /// <summary>
    /// 開關的外觀：開著時用強調底與強調框，與核取方塊的「打勾」同一組色。
    /// </summary>
    /// <remarks>
    /// 搜尋框裡那兩顆與工具列上的圖示開關（<see cref="CreateIconToggle"/>）共用它：兩處的
    /// 「開著」畫成不同的樣子，使用者要學兩次同一件事。
    ///
    /// 不沿用分段開關那一份：那一份的選取是「底槽裡浮起來的一段」，底色刻意與
    /// <see cref="ThemeBrush.ListBackground"/> 相同，而搜尋框的底色<b>正是</b>它——
    /// 疊上去之後開著與關著的差別只剩一圈髮絲線，看起來像一個沒對齊的外框而不是一個狀態。
    ///
    /// 開著與停駐的順序不能反：兩個條件同時成立時，後宣告的那一個才是使用者要看的，
    /// 而滑鼠掃過一顆開著的開關時不該讓它看起來像關掉了。
    ///
    /// 狀態不只靠顏色：圖示本身說的是哪一種比對，開關的按下狀態另由
    /// <see cref="System.Windows.Automation.TogglePattern"/> 唸得出來。
    /// </remarks>
    public static Style CreateToggleStyle()
    {
        var box = new FrameworkElementFactory(typeof(Border)) { Name = "toggle" };
        box.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        box.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(InnerRadius));
        box.SetBinding(Border.PaddingProperty, TemplatedParent(nameof(Control.Padding)));

        var label = new FrameworkElementFactory(typeof(ContentPresenter)) { Name = "label" };
        label.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        box.AppendChild(label);

        var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = box };

        AddTrigger(template, UIElement.IsMouseOverProperty, Border.BackgroundProperty, ThemeBrush.RowHover, "toggle");

        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, ThemeBrush.AccentBackground, "toggle"));
        on.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.AccentBorder, "toggle"));
        template.Triggers.Add(on);

        AddTrigger(template, ButtonBase.IsPressedProperty, Border.BackgroundProperty, ThemeBrush.RowPressed, "toggle");
        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty, Border.BorderBrushProperty, ThemeBrush.AccentBorder, "toggle");

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);

        var style = new Style(typeof(ToggleButton));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, InterfaceFont));
        style.Setters.Add(new Setter(Control.FontSizeProperty, DefaultMetrics.Caption));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 22d));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.ListForeground));
        return style;
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
}
