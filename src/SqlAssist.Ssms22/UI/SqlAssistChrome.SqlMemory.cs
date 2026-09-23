using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.UI;

internal static partial class SqlAssistChrome
{
    // 語意值決定圖示，不依賴可翻譯的顯示文字；篩選、卡片與 Preview 都查同一份對應。
    // 沒有對應就擲出，不退回某一顆看似合理的圖示把漏掉的選項藏起來。
    public static SqlIcon MemoryOptionIcon(object value) => value switch
    {
        SqlHistoryFilter.Executions => SqlIcon.Execute, SqlHistoryFilter.Drafts => SqlIcon.Edit, SqlHistoryFilter.All => SqlIcon.All,
        SqlHistoryPeriod.Any => SqlIcon.AnyTime, SqlHistoryPeriod => SqlIcon.Calendar,
        SqlConnectionFacetSort.Recent or SqlConnectionFacetSort.ReverseAlphabetical => SqlIcon.SortDescending,
        SqlConnectionFacetSort.Oldest or SqlConnectionFacetSort.Alphabetical => SqlIcon.SortAscending,
        _ => throw new System.ArgumentOutOfRangeException(nameof(value), value, "這個選項值沒有對應的圖示。")
    };
    // 狀態不能只靠顏色，卡片仍保留文字標籤。
    internal static ThemeBrush MemoryStatusBackground(bool executed) => executed ? ThemeBrush.AccentBackground : ThemeBrush.BadgeBackground;
    internal static ThemeBrush MemoryStatusBorder(bool executed) => executed ? ThemeBrush.AccentBorder : ThemeBrush.Hairline;

    /// <summary>用量分頁的名稱；與 History／Favorites 同樣用英文，分頁、Tooltip 與警示文字共用。</summary>
    public const string UsageTabLabel = "Usage";

    /// <summary>用量分頁：與 History／Favorites 同一種分頁，圖示右上角多一個容量分級點。</summary>
    public static TabItem CreateMemoryUsageTab()
    {
        var tab = CreateIconTab(SqlIcon.Usage, UsageTabLabel);
        var label = (DockPanel)tab.Header;
        var icon = (FrameworkElement)label.Children[0];
        label.Children.RemoveAt(0);
        var glyph = new Grid { Margin = icon.Margin, VerticalAlignment = VerticalAlignment.Center };
        icon.Margin = default;
        glyph.Children.Add(icon);
        // 點疊在圖示右上角、不佔版面；底色描邊讓它在圖示上仍分得出邊界。
        var badge = new Ellipse
        {
            Width = 7, Height = 7, StrokeThickness = 1.2, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -3, 0), IsHitTestVisible = false,
            Visibility = Visibility.Collapsed, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)
        };
        badge.SetResourceReference(Shape.StrokeProperty, ThemeBrush.WindowBackground);
        glyph.Children.Add(badge);
        DockPanel.SetDock(glyph, Dock.Left);
        label.Children.Insert(0, glyph);
        return tab;
    }

    public static FrameworkElement CreateLoadingIndicator(RotateTransform rotation)
    {
        var arc = new Path { Data = Geometry.Parse("M 18,10 A 8,8 0 1 1 10,2"), Width = 20, Height = 20,
            StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rotation };
        arc.SetResourceReference(Shape.StrokeProperty, ThemeBrush.ListForeground);
        var indicator = new Border { Child = arc, Padding = new Thickness(8), CornerRadius = new CornerRadius(18),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        indicator.SetResourceReference(Border.BackgroundProperty, ThemeBrush.ListBackground);
        AutomationProperties.SetName(indicator, "載入中");
        return indicator;
    }

    /// <summary>
    /// SQL Memory 的第一列：左邊是分頁，右邊只有設定。
    /// </summary>
    /// <remarks>
    /// 作用在<b>目前這一份</b>的操作不在這裡：重新整理跟著搜尋列走（見 <see cref="SqlInputRow"/>），
    /// 套用查詢視窗連線的那一顆跟著伺服器篩選走（見 <see cref="CreateMemoryFilterRow"/>）。分頁列回答的是「在看哪一種東西」，而設定是跨分頁的
    /// 共通設定，兩者都不隨分頁換意思。混在一起的那一版讓使用者在切到用量分頁之後，
    /// 還得先看懂那顆重新整理現在是在整理什麼。
    /// </remarks>
    public static Grid CreateMemoryToolbar(TabControl tabs, Button settings)
    {
        var toolbar = new Grid { MinHeight = 32, Margin = new Thickness(0, 0, 0, 6) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabs.Template = CreateTabControlTemplate(compact: true);
        tabs.HorizontalAlignment = HorizontalAlignment.Left; tabs.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(tabs);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 1); toolbar.Children.Add(actions);
        var content = CreateIconLabel(SqlIcon.Settings, "設定");
        var label = (TextBlock)content.Children[1];
        settings.Content = content; settings.ToolTip = "設定"; AutomationProperties.SetName(settings, "設定");
        settings.Height = 28; settings.MinWidth = 28; settings.Padding = new Thickness(6, 3, 6, 3);
        settings.Margin = new Thickness(4, 0, 0, 0); actions.Children.Add(settings);
        // 窄窗先收起設定的文字，再收起分頁文字；不換行、不改變高度，維持分頁與圖示共用中心線。
        // 分頁文字依實際寬度決定：分頁數會變，寫死門檻遲早又讓分頁列折成兩行。
        void UpdateLabels()
        {
            var width = toolbar.ActualWidth;
            label.Visibility = width >= MemoryActionLabelWidth ? Visibility.Visible : Visibility.Collapsed;
            SetTabLabels(tabs, Visibility.Visible);
            tabs.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (tabs.DesiredSize.Width + actions.DesiredSize.Width > width) SetTabLabels(tabs, Visibility.Collapsed);
            // 量測只為了比寬度；交回版面系統用欄寬重新量測，不沿用無限寬的結果。
            tabs.InvalidateMeasure(); actions.InvalidateMeasure();
        }
        // 按鈕列只隨寬度門檻改變，分頁文字不影響它，不會互相觸發。
        toolbar.SizeChanged += (_, _) => UpdateLabels();
        actions.SizeChanged += (_, e) => { if (e.WidthChanged) UpdateLabels(); };
        return toolbar;
    }

    /// <summary>窄到這裡以下設定只留圖示；分頁文字再窄一階才收，兩者不同時消失。</summary>
    private const double MemoryActionLabelWidth = 420d;

    private static void SetTabLabels(TabControl tabs, Visibility visibility)
    {
        foreach (var item in tabs.Items)
            if (item is TabItem { Header: DockPanel { Children.Count: 2 } header } && header.Children[1] is TextBlock text)
                text.Visibility = visibility;
    }

    /// <summary>
    /// 用量分頁圖示右上角的分級點；容量正常時不顯示，偏高與接近上限各用語意色。
    /// </summary>
    /// <remarks>
    /// 點本身只是提醒，分級文字在 Tooltip 與 automation help text；窄窗收起文字時仍看得到。
    /// 出現時做一次 240 ms 的縮放，屬於狀態回饋；同一分級重複設定不重播。
    /// </remarks>
    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    public static void SetUsageBadge(TabItem usage, SqlMemoryUsageSeverity severity, bool? motion = null)
    {
        if (UsageBadge(usage) is not { } badge) return;
        var visible = severity != SqlMemoryUsageSeverity.Normal;
        var text = severity switch
        {
            SqlMemoryUsageSeverity.Critical => UsageTabLabel + "：容量接近或超過上限",
            SqlMemoryUsageSeverity.Warning => UsageTabLabel + "：容量偏高",
            _ => UsageTabLabel,
        };
        usage.ToolTip = text; AutomationProperties.SetHelpText(usage, visible ? text : "");
        if (!visible) { badge.Visibility = Visibility.Collapsed; badge.Tag = null; return; }
        badge.SetResourceReference(Shape.FillProperty, SqlUsageMeter.Brush(severity));
        var changed = !Equals(badge.Tag, severity);
        badge.Tag = severity; badge.Visibility = Visibility.Visible;
        var scale = (ScaleTransform)badge.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!changed || !(motion ?? MotionEnabled)) return;
        var pop = new DoubleAnimationUsingKeyFrames { Duration = UsageBadgePop, FillBehavior = FillBehavior.Stop };
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.3, KeyTime.FromPercent(0.6), new CubicEase { EasingMode = EasingMode.EaseOut }));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop); scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    public static readonly System.TimeSpan UsageBadgePop = System.TimeSpan.FromMilliseconds(240);

    internal static Ellipse? UsageBadge(TabItem usage) =>
        usage.Header is DockPanel { Children.Count: > 0 } content && content.Children[0] is Grid { Children.Count: 2 } glyph
            ? glyph.Children[1] as Ellipse
            : null;

    /// <summary>前置語意圖示、輸入欄與尾端按鈕共用一個外框；搜尋列與收藏標註欄位同一種外觀。</summary>
    /// <param name="extra">
    /// 尾端按鈕左邊還要放的控制項，依陣列順序由左往右。修飾「這個字串怎麼比」的開關
    /// （大小寫、全字）放在這裡，不佔工具列的寬度。
    /// </param>
    public static Border CreateInputBar(SqlIcon icon, TextBox input, Button trailing, params FrameworkElement[] extra)
    {
        var panel = new DockPanel();
        var glyph = CreateIcon(icon); glyph.Margin = new Thickness(8, 0, 4, 0);
        DockPanel.SetDock(glyph, Dock.Left); panel.Children.Add(glyph);
        DockPanel.SetDock(trailing, Dock.Right); panel.Children.Add(trailing);
        // 由後往前停靠：DockPanel 讓先停的那一個吃到最右邊，而陣列的順序要看起來是由左往右。
        for (var index = extra.Length - 1; index >= 0; index--)
        {
            DockPanel.SetDock(extra[index], Dock.Right); panel.Children.Add(extra[index]);
        }
        // 保留原生編輯語意，但外框只畫一次；鍵盤焦點由整條搜尋列呈現。
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        input.Template = new ControlTemplate(typeof(TextBox)) { VisualTree = host };
        input.Padding = new Thickness(4); input.BorderThickness = new Thickness(0);
        input.VerticalContentAlignment = VerticalAlignment.Center;
        panel.Children.Add(input);
        var border = new Border { Child = panel, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), MinHeight = 30 };
        var style = new Style(typeof(Border));
        style.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, ThemeBrush.ListBackground));
        style.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.Hairline));
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.AccentBorder));
        style.Triggers.Add(focus); border.Style = style;
        return border;
    }

    /// <summary>
    /// SQL Memory 的範圍列：連線、狀態與期間各一群，走共用的 <see cref="SqlFilterBar"/>，排在搜尋列上面。
    /// </summary>
    /// <remarks>
    /// 連線那一群排第一，套用查詢視窗連線的那一顆在伺服器左邊（與 SQL Search 同一個位置）：
    /// 它一次換掉伺服器與資料庫兩個條件，是這兩顆的捷徑，放在搜尋框右緣的那一版讓人以為它作用在搜尋字串上。
    /// 連線與狀態／期間併在同一列，不另起一列：停靠面板裡多一列等於永久少看一筆 SQL，
    /// 而放不下的時候那一層本來就會整群換行。切到 Favorites 時狀態與期間整群收起，
    /// 它們前面那一條分隔線由 <see cref="SqlFilterBar"/> 跟著收，不留一條孤線。
    /// </remarks>
    public static SqlFilterBar CreateMemoryFilterRow(
        FrameworkElement connection, FrameworkElement server, FrameworkElement database,
        SqlPillSelector kind, SqlPillSelector period)
    {
        AutomationProperties.SetName(kind, "狀態"); AutomationProperties.SetName(period, "期間");
        return new SqlFilterBar(
            new[] { connection, server, database },
            new FrameworkElement[] { kind },
            new FrameworkElement[] { period });
    }

    public static DockPanel CreateMemoryDetailBody(UIElement viewer, TextBlock status, params UIElement[] actions)
    {
        var root = new DockPanel();
        var toolbar = new WrapPanel();
        foreach (var action in actions) toolbar.Children.Add(action);
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        status.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        root.Children.Add(viewer); return root;
    }

    /// <summary>標籤在上、欄位在下，間距 4；區塊之間的距離由呼叫端的版面決定。</summary>
    /// <param name="input">真正接受輸入的控制項；外框可能包著它，自動化名稱仍要落在輸入本身。</param>
    public static DockPanel CreateMemoryField(string label, FrameworkElement field, Control input)
    {
        var panel = new DockPanel();
        var caption = CreateLabel(label, DefaultMetrics); caption.Margin = new Thickness(0, 0, 0, 4);
        DockPanel.SetDock(caption, Dock.Top); panel.Children.Add(caption);
        panel.Children.Add(field);
        AutomationProperties.SetName(input, label); return panel;
    }

    /// <summary>下拉建議的開關：沿用展開箭頭，停駐才顯色，不另畫一個 ComboBox 外框。</summary>
    public static Button CreateDropDownButton(string label)
    {
        var button = CreateButton("", DefaultMetrics);
        button.Template = CreateGhostButtonTemplate();
        button.Content = CreateChevron(); button.Padding = new Thickness(4);
        button.MinWidth = 26; button.MinHeight = 26; button.Focusable = false;
        button.ToolTip = label; AutomationProperties.SetName(button, label);
        return button;
    }

    public static Style CreateMemoryPillStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "pill" };
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.PaddingProperty, new Thickness(8, 2, 8, 2));
        border.SetResourceReference(Border.BackgroundProperty, ThemeBrush.BadgeBackground);
        border.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        border.SetBinding(TextElement.ForegroundProperty, TemplatedParent(nameof(Control.Foreground)));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(RadioButton)) { VisualTree = border };
        AddTrigger(template, UIElement.IsMouseOverProperty, Border.BackgroundProperty, ThemeBrush.RowHover, "pill");
        AddTrigger(template, UIElement.IsMouseOverProperty, TextElement.ForegroundProperty, ThemeBrush.SelectedForeground, "pill");
        AddTrigger(template, ToggleButton.IsCheckedProperty, Border.BackgroundProperty, ThemeBrush.RowSelected, "pill");
        AddTrigger(template, ToggleButton.IsCheckedProperty, Border.BorderBrushProperty, ThemeBrush.AccentBorder, "pill");
        AddTrigger(template, ToggleButton.IsCheckedProperty, TextElement.ForegroundProperty, ThemeBrush.SelectedForeground, "pill");
        foreach (var property in new[] { ToggleButton.IsCheckedProperty, UIElement.IsMouseOverProperty })
        {
            var foreground = new Trigger { Property = property, Value = true };
            foreground.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.SelectedForeground));
            template.Triggers.Add(foreground);
        }
        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty, Border.BorderBrushProperty, ThemeBrush.AccentBorder, "pill");
        AddTrigger(template, ButtonBase.IsPressedProperty, Border.BackgroundProperty, ThemeBrush.RowPressed, "pill");
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45)); template.Triggers.Add(disabled);
        var style = new Style(typeof(RadioButton));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, InterfaceFont));
        style.Setters.Add(new Setter(Control.FontSizeProperty, DefaultMetrics.Caption));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 2, 4, 2)));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 24d));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.ListForeground));
        return style;
    }

    /// <summary>
    /// 清單頁尾的膠囊按鈕：比一般幽靈按鈕多一條細框，讓「還有更多」在清單底部仍讀得出是可按的。
    /// </summary>
    public static Button CreateMemoryPagerButton()
    {
        var shell = new FrameworkElementFactory(typeof(Border)) { Name = "pill" };
        shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        shell.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        shell.SetBinding(Border.PaddingProperty, TemplatedParent(nameof(Control.Padding)));
        shell.SetBinding(TextElement.ForegroundProperty, TemplatedParent(nameof(Control.Foreground)));
        shell.SetResourceReference(Border.BackgroundProperty, ThemeBrush.ListBackground);
        shell.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        shell.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = shell };
        AddTrigger(template, UIElement.IsMouseOverProperty, Border.BackgroundProperty, ThemeBrush.RowHover, "pill");
        AddTrigger(template, UIElement.IsMouseOverProperty, Border.BorderBrushProperty, ThemeBrush.Border, "pill");
        AddTrigger(template, UIElement.IsMouseOverProperty, Control.ForegroundProperty, ThemeBrush.SelectedForeground);
        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty, Border.BorderBrushProperty, ThemeBrush.AccentBorder, "pill");
        AddTrigger(template, ButtonBase.IsPressedProperty, Border.BackgroundProperty, ThemeBrush.RowPressed, "pill");
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.7, "pill")); template.Triggers.Add(disabled);
        var style = new Style(typeof(Button));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.ListForeground));
        return new Button
        {
            Template = template, Style = style, FontFamily = InterfaceFont, FontSize = DefaultMetrics.Caption,
            Padding = new Thickness(14, 3, 14, 3), MinHeight = 28, MinWidth = 132, FocusVisualStyle = null,
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    /// <summary>頁尾兩側的細線；中央摘要把清單的結尾讀成一個段落，而不是另一張卡片。</summary>
    public static Border CreateMemoryPagerRule()
    {
        var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true, Opacity = 0.9 };
        rule.SetResourceReference(Border.BackgroundProperty, ThemeBrush.Hairline);
        return rule;
    }

    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    public static DataTemplate CreateSqlSummaryTemplate(bool? motion = null)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        // 第一列：檔名 → 狀態 → 次數 → 彈性空白 → 伺服器 → 資料庫 → 時間，操作浮在右緣。
        // 疊層、宣告順序、操作層與窄版降級由 SqlRowHeading 擔保，這裡只填欄位。
        var row = BeginRowHeading(panel);
        var heading = row.Heading;
        var connections = new FrameworkElementFactory(typeof(DockPanel)); connections.SetValue(DockPanel.DockProperty, Dock.Right);
        // 整組靠右，組內一律靠左排，順序才是「伺服器 → 資料庫 → 時間」；宣告順序同時是窄窗下縮的順序。
        connections.SetValue(DockPanel.LastChildFillProperty, false); heading.AppendChild(connections);
        AppendConnectionBadges(connections);
        var time = BoundText("RelativeTime"); time.Name = "time";
        time.SetValue(FrameworkElement.MaxWidthProperty, 136d);
        time.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        time.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        time.SetBinding(FrameworkElement.ToolTipProperty, new Binding("TimeSummary")); connections.AppendChild(time);
        var identity = row.Identity;
        var count = CountBadge(); count.SetValue(DockPanel.DockProperty, Dock.Right); identity.AppendChild(count);
        var state = CreateBadge("Status", "state", iconProperty: "StatusIcon"); state.SetValue(DockPanel.DockProperty, Dock.Right); identity.AppendChild(state);
        var title = BoundText("Name"); title.Name = "name"; title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(FrameworkElement.MaxWidthProperty, RowNameMaxWidth);
        title.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0)); identity.AppendChild(title);
        var code = new FrameworkElementFactory(typeof(Border)); code.SetResourceReference(Border.BackgroundProperty, ThemeBrush.RowAlternate);
        code.SetValue(Border.CornerRadiusProperty, new CornerRadius(4)); code.SetValue(Border.PaddingProperty, new Thickness(6, 2, 6, 2));
        code.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 3));
        var sql = BoundText("Preview"); sql.SetValue(TextBlock.FontFamilyProperty, CodeFont);
        sql.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        sql.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap); sql.SetValue(TextBlock.LineHeightProperty, 16d);
        sql.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
        sql.SetValue(FrameworkElement.MaxHeightProperty, 16d); code.AppendChild(sql); panel.AppendChild(code);
        var favorite = new DataTrigger { Binding = new Binding("IsFavorite"), Value = true };
        var history = new DataTrigger { Binding = new Binding("IsFavorite"), Value = false };
        // 窄版：連線膠囊降成 icon-only，次要操作收進 overflow；主要動作與名稱一直看得見。
        var narrow = row.Narrow;
        IconOnlyInNarrow(narrow, "server"); IconOnlyInNarrow(narrow, "database");
        foreach (var command in SqlMemoryRowCommand.All)
        {
            var button = CreateRowActionButton("action" + command.Action, command.Action, command.Icon, command.Label, command.Tone, command.IsSeparated);
            if (command.LabelProperty is { } labelProperty)
            {
                button.SetBinding(FrameworkElement.ToolTipProperty, new Binding(labelProperty));
                button.SetBinding(AutomationProperties.NameProperty, new Binding(labelProperty));
            }
            // 不適用的操作直接收起，不留停用的灰色按鈕；判斷來源與快捷選單、Preview 相同。
            if (command.Kind == SqlMemoryRowKind.History) favorite.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, button.Name));
            else if (command.Kind == SqlMemoryRowKind.Favorite) history.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, button.Name));
            if (!command.IsPrimary)
                narrow.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, button.Name));
            row.Actions.AppendChild(button);
        }
        // 多選模式才出現的勾選欄在整列左邊，跨過兩列內容；平常收起，版面與沒有多選時相同。
        var template = new DataTemplate { VisualTree = WrapWithRowCheck(panel, "Name") };
        template.Triggers.Add(favorite); template.Triggers.Add(history);
        CollapseEmptyConnectionBadges(template);
        CollapseSingleExecution(template);
        var executed = new DataTrigger { Binding = new Binding("IsExecuted"), Value = true };
        executed.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, MemoryStatusBackground(true), "state"));
        executed.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, MemoryStatusBorder(true), "state")); template.Triggers.Add(executed);
        foreach (var property in new[] { "IsSelected", "IsMouseOver" })
        {
            var selected = new DataTrigger { Binding = new Binding(property) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) }, Value = true };
            selected.Setters.Add(ThemeResourceSet.Setter(TextBlock.ForegroundProperty, ThemeBrush.SelectedForeground, "time")); template.Triggers.Add(selected);
        }
        // 掛上左半、補 overflow、疊上操作層，並接上底色鏡射與揭露；與 SQL Search 的結果列同一份。
        row.Complete(template, motion);
        RevealRowCheck(template);
        return template;
    }

    private static void AppendConnectionBadges(FrameworkElementFactory panel)
    {
        panel.AppendChild(CreateBadge("Server", "server", SqlIcon.Server));
        panel.AppendChild(CreateBadge("Database", "database", SqlIcon.Database));
    }

    /// <summary>沒有標註的收藏不畫空膠囊；History 沒有連線時列上仍有「無伺服器」這類說明文字，不受影響。</summary>
    private static void CollapseEmptyConnectionBadges(DataTemplate template)
    {
        foreach (var (property, name) in new[] { ("Server", "server"), ("Database", "database") })
        {
            var empty = new DataTrigger { Binding = new Binding(property), Value = "" };
            empty.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, name)); template.Triggers.Add(empty);
        }
    }

    /// <summary>
    /// 連續相同執行的「×N」：沒有圖示的精簡中性膠囊，外框與高度沿用連線膠囊。回答的是次數，
    /// 不借執行狀態的強調色；只有一次時收起，不在每張卡片留「×1」。
    /// </summary>
    private static FrameworkElementFactory CountBadge() =>
        CreateTextBadge("ExecutionCountText", "count", "ExecutionCountToolTip");

    private static void CollapseSingleExecution(DataTemplate template)
    {
        var single = new DataTrigger { Binding = new Binding("ExecutionCountText"), Value = "" };
        single.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "count")); template.Triggers.Add(single);
    }

    public static DataTemplate CreateMemoryMetadataTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        // 與清單列同一個順序：檔名 → 狀態 → 次數 → 伺服器 → 資料庫 → 時間。兩個表面各排各的話，
        // 使用者在清單上選一筆、眼睛移到資訊列，同一組事實卻換了位置，等於每一次都要重讀一遍。
        var name = BoundText("Name"); name.Name = "name";
        name.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        name.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground); panel.AppendChild(name);
        panel.AppendChild(CreateBadge("Status", "state", iconProperty: "StatusIcon"));
        panel.AppendChild(CountBadge());
        AppendConnectionBadges(panel);
        var time = BoundText("TimeSummary"); time.Name = "Timestamp";
        time.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
        time.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground); panel.AppendChild(time);
        var template = new DataTemplate { VisualTree = panel };
        var noTime = new DataTrigger { Binding = new Binding("TimeSummary"), Value = "" };
        noTime.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "Timestamp")); template.Triggers.Add(noTime);
        CollapseEmptyConnectionBadges(template);
        CollapseSingleExecution(template);
        var executed = new DataTrigger { Binding = new Binding("IsExecuted"), Value = true };
        executed.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, MemoryStatusBackground(true), "state"));
        executed.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, MemoryStatusBorder(true), "state")); template.Triggers.Add(executed);
        return template;
    }
}
