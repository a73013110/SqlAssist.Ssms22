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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Geometry> MemoryIconGeometries = new();
    // 語意值決定圖示，不依賴可翻譯的顯示文字；篩選、卡片與 Preview 都查同一份對應。
    public static string MemoryOptionIcon(object value) => value switch
    {
        SqlHistoryFilter.Executions => "Execute", SqlHistoryFilter.Drafts => "Edit", SqlHistoryFilter.All => "All",
        SqlHistoryPeriod.Any => "Any", SqlHistoryPeriod => "Calendar",
        SqlFavoriteScope.Server => "Server", SqlFavoriteScope.Database => "Database", SqlFavoriteScope.Global => "Global",
        SqlConnectionFacetSort.Recent => "Recent", SqlConnectionFacetSort.Oldest => "Oldest",
        SqlConnectionFacetSort.Alphabetical => "Alphabetical", SqlConnectionFacetSort.ReverseAlphabetical => "ReverseAlphabetical",
        _ => "History"
    };
    // 狀態不能只靠顏色，卡片仍保留文字標籤。
    internal static ThemeBrush MemoryStatusBackground(bool executed) => executed ? ThemeBrush.AccentBackground : ThemeBrush.BadgeBackground;
    internal static ThemeBrush MemoryStatusBorder(bool executed) => executed ? ThemeBrush.AccentBorder : ThemeBrush.Hairline;

    public static Geometry MemoryIconGeometry(string name) => MemoryIconGeometries.GetOrAdd(name, CreateMemoryIconGeometry);

    private static Geometry CreateMemoryIconGeometry(string name)
    {
        var geometry = Geometry.Parse(name switch
        {
        "Search" => "M 10,6 A 4,4 0 1 1 2,6 A 4,4 0 1 1 10,6 M 9,9 L 14,14",
        "All" => "M 2,2 L 6,2 6,6 2,6 Z M 10,2 L 14,2 14,6 10,6 Z M 2,10 L 6,10 6,14 2,14 Z M 10,10 L 14,10 14,14 10,14 Z",
        "Execute" => "M 4,2 L 13,8 4,14 Z",
        "Calendar" => "M 2,4 L 14,4 14,14 2,14 Z M 5,1 L 5,6 M 11,1 L 11,6 M 2,8 L 14,8",
        "Any" => "M 8,8 C 3,-1 -3,8 3,11 C 6,13 10,1 13,5 C 18,11 11,16 8,8",
        "Server" => "M 2,2 L 14,2 14,7 2,7 Z M 2,9 L 14,9 14,14 2,14 Z M 5,4 L 5,5 M 5,11 L 5,12",
        "Database" => "M 2,4 A 6,2 0 1 1 14,4 A 6,2 0 1 1 2,4 L 2,12 A 6,2 0 0 0 14,12 L 14,4 M 2,8 A 6,2 0 0 0 14,8",
        "Global" => "M 14,8 A 6,6 0 1 1 2,8 A 6,6 0 1 1 14,8 M 2,8 L 14,8 M 8,2 C 3,6 3,10 8,14 C 13,10 13,6 8,2",
        "Recent" => "M 2,3 L 9,3 M 2,7 L 7,7 M 2,11 L 5,11 M 12,2 L 12,14 M 9,11 L 12,14 15,11",
        "Oldest" => "M 2,3 L 9,3 M 2,7 L 7,7 M 2,11 L 5,11 M 12,2 L 12,14 M 9,5 L 12,2 15,5",
        "Alphabetical" => "M 1,3 L 6,3 M 1,7 L 8,7 M 1,11 L 10,11 M 13,2 L 13,14 M 10,11 L 13,14 16,11",
        "ReverseAlphabetical" => "M 1,3 L 10,3 M 1,7 L 8,7 M 1,11 L 6,11 M 13,2 L 13,14 M 10,5 L 13,2 16,5",
        "Clear" => "M 4,4 L 12,12 M 12,4 L 4,12",
        "Copy" => "M 6,5 L 13,5 13,14 6,14 Z M 10,5 L 10,2 3,2 3,11 6,11",
        "Open" => "M 9,2 L 14,2 14,7 M 14,2 L 7,9 M 6,3 L 2,3 2,14 13,14 13,10",
        "Favorite" => "M 8,1 L 10,5 15,6 11.5,9.5 12.5,15 8,12.5 3.5,15 4.5,9.5 1,6 6,5 Z",
        "Edit" => "M 3,10 L 11,2 14,5 6,13 2,14 Z M 9,4 L 12,7",
        "Remove" => "M 3,4 L 13,4 M 6,2 L 10,2 M 4,4 L 5,14 11,14 12,4 M 7,6 L 7,12 M 9,6 L 9,12",
        "Wrap" => "M 2,3 L 14,3 M 2,7 L 11,7 Q 15,7 14,10 Q 14,12 10,12 L 7,12 M 9,10 L 7,12 9,14 M 2,11 L 4,11",
        "Connection" => "M 8,1 L 8,4 M 8,12 L 8,15 M 1,8 L 4,8 M 12,8 L 15,8 M 13,8 A 5,5 0 1 1 3,8 A 5,5 0 1 1 13,8 M 9,8 A 1,1 0 1 1 7,8 A 1,1 0 1 1 9,8",
        "Chevron" => "M 3,5 L 8,10 13,5",
        "Refresh" => "M 13,6 A 5.5,5.5 0 1 0 13,11 M 13,2 L 13,6 9,6",
        "Settings" => "M 6,1 L 10,1 10.5,3 12,4 14,4 15,7 13.5,8.5 13,10 14,12 11,14 9.5,12.5 7.5,13 6,15 3,13.5 3,11.5 2,10 0.5,9 1.5,6 3.5,5.5 5,4 Z M 10.5,8 A 2.5,2.5 0 1 1 5.5,8 A 2.5,2.5 0 1 1 10.5,8",
        _ => "M 8,2 A 6,6 0 1 1 2,8 M 8,4 L 8,8 11,10"
        }).Clone();
        // Shape.Stretch.Uniform 只縮放 bounds，不保證線條在固定 slot 裡置中；先正規化幾何再以 None 畫出。
        var bounds = geometry.Bounds;
        var scale = 12 / System.Math.Max(bounds.Width, bounds.Height);
        geometry.Transform = new MatrixTransform(scale, 0, 0, scale,
            7 - (bounds.X + bounds.Width / 2) * scale, 7 - (bounds.Y + bounds.Height / 2) * scale);
        geometry.Freeze(); return geometry;
    }

    public static SqlMemoryIcon CreateMemoryIcon(string name) => new() { IconName = name };

    public static Button CreateMemoryIconButton(string icon, string label)
    {
        var button = CreateButton("", DefaultMetrics);
        button.Content = CreateMemoryButtonIcon(icon); button.ToolTip = label;
        button.Padding = new Thickness(5); button.MinWidth = 26; button.MinHeight = 26;
        AutomationProperties.SetName(button, label);
        return button;
    }

    // Content 的邏輯父層一定是所屬 Control；不能依賴尚未建立或重掛的樣板視覺祖先。
    // 狀態色在 Control 的共用樣板處切換，也不受宿主 ContentPresenter 隱含樣式影響。
    private static Binding MemoryButtonForeground() => new Binding
    {
        Path = new PropertyPath(Control.ForegroundProperty),
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1)
    };

    public static SqlMemoryIcon CreateMemoryButtonIcon(string name)
    {
        var icon = CreateMemoryIcon(name); icon.SetBinding(TextElement.ForegroundProperty, MemoryButtonForeground()); return icon;
    }

    public static SqlMemoryIcon CreateMemoryMenuIcon(string name)
    {
        var icon = CreateMemoryIcon(name);
        icon.SetBinding(TextElement.ForegroundProperty, new Binding(nameof(Control.Foreground))
        { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(MenuItem), 1) });
        return icon;
    }

    public static TextBlock CreateMemoryButtonText(string text)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetBinding(TextBlock.ForegroundProperty, MemoryButtonForeground()); return label;
    }

    public static DockPanel CreateMemoryLabel(string icon, string text)
    {
        var content = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        var glyph = CreateMemoryButtonIcon(icon); glyph.Margin = new Thickness(0, 0, 5, 0);
        DockPanel.SetDock(glyph, Dock.Left); content.Children.Add(glyph);
        content.Children.Add(CreateMemoryButtonText(text));
        return content;
    }

    public static TabItem CreateMemoryTab(string icon, string label)
    {
        var style = new Style(typeof(TabItem));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.DimForeground));
        var tab = new TabItem { Header = CreateMemoryLabel(icon, label), Template = CreateTabItemTemplate(), Style = style };
        AutomationProperties.SetName(tab, label); return tab;
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

    public static Button CreateMemoryConnectionButton()
    {
        var button = CreateButton("", DefaultMetrics);
        button.Content = CreateMemoryLabel("Connection", "目前連線");
        button.ToolTip = "使用目前作用中 SQL 查詢視窗的伺服器與資料庫篩選；不切換連線。";
        AutomationProperties.SetName(button, "以目前連線篩選");
        return button;
    }

    public static Grid CreateMemoryToolbar(TabControl tabs, Button connection, Button refresh, Button settings)
    {
        var toolbar = new Grid { MinHeight = 32, Margin = new Thickness(0, 0, 0, 6) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabs.Template = CreateTabControlTemplate(compact: true);
        tabs.HorizontalAlignment = HorizontalAlignment.Left; tabs.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(tabs);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 1); toolbar.Children.Add(actions);
        var labels = new System.Collections.Generic.List<TextBlock>();
        var connectionLabel = (TextBlock)((Panel)connection.Content).Children[1];
        foreach (var entry in new[] { (refresh, "Refresh", "重新整理"), (settings, "Settings", "設定") })
        {
            var content = CreateMemoryLabel(entry.Item2, entry.Item3);
            labels.Add((TextBlock)content.Children[1]); entry.Item1.Content = content;
            entry.Item1.ToolTip = entry.Item3; AutomationProperties.SetName(entry.Item1, entry.Item3);
        }
        foreach (var button in new[] { connection, refresh, settings })
        {
            button.Height = 28; button.MinWidth = 28; button.Padding = new Thickness(6, 3, 6, 3);
            button.Margin = new Thickness(4, 0, 0, 0); actions.Children.Add(button);
        }
        // 窄窗只收起次要操作文字，不換行或改變按鈕高度，維持分頁與圖示共用中心線。
        void UpdateLabels()
        {
            foreach (var label in labels) label.Visibility = toolbar.ActualWidth >= 520 ? Visibility.Visible : Visibility.Collapsed;
            connectionLabel.Visibility = toolbar.ActualWidth >= 380 ? Visibility.Visible : Visibility.Collapsed;
        }
        toolbar.SizeChanged += (_, _) => UpdateLabels(); UpdateLabels();
        return toolbar;
    }

    public static Border CreateSearchBar(TextBox input, Button clear)
    {
        var panel = new DockPanel();
        var icon = CreateMemoryIcon("Search"); icon.Margin = new Thickness(8, 0, 4, 0);
        DockPanel.SetDock(icon, Dock.Left); panel.Children.Add(icon);
        DockPanel.SetDock(clear, Dock.Right); panel.Children.Add(clear);
        // 保留原生編輯語意，但外框只畫一次；鍵盤焦點由整條搜尋列呈現。
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        input.Template = new ControlTemplate(typeof(TextBox)) { VisualTree = host };
        input.Padding = new Thickness(4); input.BorderThickness = new Thickness(0);
        input.VerticalContentAlignment = VerticalAlignment.Center;
        panel.Children.Add(input);
        var border = new Border { Child = panel, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 6), MinHeight = 30 };
        var style = new Style(typeof(Border));
        style.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, ThemeBrush.ListBackground));
        style.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.Hairline));
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.AccentBorder));
        style.Triggers.Add(focus); border.Style = style;
        return border;
    }

    public static FrameworkElement CreateMemoryHistoryFilters(SqlPillSelector kind, SqlPillSelector period)
    {
        var groups = new WrapPanel();
        AutomationProperties.SetName(kind, "狀態"); AutomationProperties.SetName(period, "期間");
        groups.Children.Add(kind);
        var separator = new Border { Child = period, BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(8, 0, 0, 0), Margin = new Thickness(4, 0, 0, 0) };
        separator.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        groups.Children.Add(separator); return groups;
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

    public static ComboBox CreateMemoryScopeCombo(params string[] labels)
    {
        var combo = CreateComboBox(DefaultMetrics);
        foreach (var label in labels) combo.Items.Add(label);
        combo.SelectedIndex = 0; return combo;
    }

    public static StackPanel CreateMemoryField(string label, Control control)
    {
        var panel = new StackPanel(); panel.Children.Add(CreateLabel(label, DefaultMetrics));
        control.Margin = new Thickness(0, 4, 0, 0); panel.Children.Add(control);
        AutomationProperties.SetName(control, label); return panel;
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

    public static Style CreateSqlCardStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "card" };
        border.SetBinding(TextElement.ForegroundProperty, TemplatedParent(nameof(Control.Foreground)));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.PaddingProperty, new Thickness(8, 4, 8, 4));
        border.SetResourceReference(Border.BackgroundProperty, ThemeBrush.ListBackground);
        border.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        var layers = new FrameworkElementFactory(typeof(Grid));
        var hoverLayer = new FrameworkElementFactory(typeof(Border)) { Name = "hoverTint" };
        hoverLayer.SetValue(UIElement.OpacityProperty, 0d);
        hoverLayer.SetValue(UIElement.IsHitTestVisibleProperty, false);
        hoverLayer.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        hoverLayer.SetResourceReference(Border.BackgroundProperty, ThemeBrush.RowHover);
        layers.AppendChild(hoverLayer); layers.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        border.AppendChild(layers);
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
        {
            foreach (var enter in new[] { true, false })
            {
                var animation = new DoubleAnimation(enter ? 1 : 0, new Duration(System.TimeSpan.FromMilliseconds(100)));
                Storyboard.SetTargetName(animation, "hoverTint"); Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));
                var storyboard = new Storyboard(); storyboard.Children.Add(animation);
                var trigger = new EventTrigger(enter ? UIElement.MouseEnterEvent : UIElement.MouseLeaveEvent);
                trigger.Actions.Add(new BeginStoryboard { Storyboard = storyboard }); template.Triggers.Add(trigger);
            }
        }
        else AddTrigger(template, UIElement.IsMouseOverProperty, Border.BackgroundProperty, ThemeBrush.RowHover, "card");
        AddTrigger(template, UIElement.IsMouseOverProperty, TextElement.ForegroundProperty, ThemeBrush.SelectedForeground, "card");
        AddTrigger(template, UIElement.IsMouseOverProperty, Control.ForegroundProperty, ThemeBrush.SelectedForeground);
        AddTrigger(template, ListBoxItem.IsSelectedProperty, Border.BackgroundProperty, ThemeBrush.RowSelected, "card");
        AddTrigger(template, ListBoxItem.IsSelectedProperty, TextElement.ForegroundProperty, ThemeBrush.SelectedForeground, "card");
        AddTrigger(template, ListBoxItem.IsSelectedProperty, Control.ForegroundProperty, ThemeBrush.SelectedForeground);
        AddTrigger(template, ListBoxItem.IsSelectedProperty, Border.BorderBrushProperty, ThemeBrush.AccentBorder, "card");
        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty, Border.BorderBrushProperty, ThemeBrush.AccentBorder, "card");
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, InterfaceFont));
        style.Setters.Add(new Setter(Control.FontSizeProperty, DefaultMetrics.Body));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 2, 4)));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.ListForeground));
        return style;
    }

    public static DataTemplate CreateSqlSummaryTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        var heading = new FrameworkElementFactory(typeof(DockPanel)); panel.AppendChild(heading);
        var state = BoundBadge("Status", "state", iconProperty: "StatusIcon"); state.SetValue(DockPanel.DockProperty, Dock.Left); heading.AppendChild(state);
        var time = BoundText("RelativeTime"); time.Name = "time"; time.SetValue(DockPanel.DockProperty, Dock.Right);
        time.SetValue(FrameworkElement.MaxWidthProperty, 136d);
        time.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        time.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        time.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Timestamp")); heading.AppendChild(time);
        var title = BoundText("Name"); title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0)); heading.AppendChild(title);
        var code = new FrameworkElementFactory(typeof(Border)); code.SetResourceReference(Border.BackgroundProperty, ThemeBrush.RowAlternate);
        code.SetValue(Border.CornerRadiusProperty, new CornerRadius(4)); code.SetValue(Border.PaddingProperty, new Thickness(6, 2, 6, 2));
        code.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 3));
        var sql = BoundText("Preview"); sql.SetValue(TextBlock.FontFamilyProperty, CodeFont);
        sql.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        sql.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap); sql.SetValue(TextBlock.LineHeightProperty, 16d);
        sql.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
        sql.SetValue(FrameworkElement.MaxHeightProperty, 16d); code.AppendChild(sql); panel.AppendChild(code);
        var footer = new FrameworkElementFactory(typeof(DockPanel)); panel.AppendChild(footer);
        var actions = new FrameworkElementFactory(typeof(StackPanel)) { Name = "actions" };
        actions.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); actions.SetValue(DockPanel.DockProperty, Dock.Right);
        // 動作列沿用卡片表面；透明底不會切斷 hover／selected 的底色與動畫。
        // Hidden 保留尺寸，避免懸停時 badge 跳動；鍵盤進入卡片也揭露動作。
        actions.SetValue(UIElement.VisibilityProperty, Visibility.Hidden); footer.AppendChild(actions);
        foreach (var pair in new[] { ("Copy", "複製 SQL"), ("Open", "在新 Query 開啟（不執行）"), ("Favorite", "Add to Favorites") })
        {
            var button = new FrameworkElementFactory(typeof(Button));
            button.SetValue(FrameworkElement.TagProperty, pair.Item1); button.SetValue(FrameworkElement.ToolTipProperty, pair.Item2);
            button.SetValue(AutomationProperties.NameProperty, pair.Item2);
            button.SetValue(Control.TemplateProperty, CreateGhostButtonTemplate()); button.SetValue(Control.PaddingProperty, new Thickness(3));
            // 動作列不再有實色底，前景必須跟隨卡片的 hover／selected 配對色（尤其高對比）。
            button.SetBinding(Control.ForegroundProperty, MemoryButtonForeground());
            button.SetValue(FrameworkElement.WidthProperty, 24d); button.SetValue(FrameworkElement.HeightProperty, 22d);
            if (pair.Item1 == "Favorite")
            {
                button.Name = "favoriteAction";
                button.SetBinding(UIElement.IsEnabledProperty, new Binding("CanAddFavorite"));
                button.SetBinding(FrameworkElement.ToolTipProperty, new Binding("AddFavoriteHint"));
                button.SetValue(ToolTipService.ShowOnDisabledProperty, true);
            }
            var icon = new FrameworkElementFactory(typeof(SqlMemoryIcon)); icon.SetValue(SqlMemoryIcon.IconNameProperty, pair.Item1);
            icon.SetBinding(TextElement.ForegroundProperty, MemoryButtonForeground());
            button.AppendChild(icon); actions.AppendChild(button);
        }
        var connections = new FrameworkElementFactory(typeof(WrapPanel)); footer.AppendChild(connections);
        connections.AppendChild(BoundBadge("Server", "server", iconProperty: "ServerIcon")); connections.AppendChild(BoundBadge("Database", "database", "Database"));
        var template = new DataTemplate { VisualTree = panel };
        var favorite = new DataTrigger { Binding = new Binding("IsFavorite"), Value = true };
        favorite.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "favoriteAction")); template.Triggers.Add(favorite);
        var noDatabase = new DataTrigger { Binding = new Binding("Database"), Value = "" };
        noDatabase.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "database")); template.Triggers.Add(noDatabase);
        var executed = new DataTrigger { Binding = new Binding("IsExecuted"), Value = true };
        executed.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, MemoryStatusBackground(true), "state"));
        executed.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, MemoryStatusBorder(true), "state")); template.Triggers.Add(executed);
        foreach (var property in new[] { "IsSelected", "IsMouseOver" })
        {
            var selected = new DataTrigger { Binding = new Binding(property) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) }, Value = true };
            selected.Setters.Add(ThemeResourceSet.Setter(TextBlock.ForegroundProperty, ThemeBrush.SelectedForeground, "time")); template.Triggers.Add(selected);
        }
        foreach (var property in new[] { "IsMouseOver", "IsKeyboardFocusWithin" })
        {
            var hover = new DataTrigger { Binding = new Binding(property) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) }, Value = true };
            hover.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "actions")); template.Triggers.Add(hover);
        }
        return template;
    }

    private static FrameworkElementFactory BoundText(string property)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(property)); text.SetBinding(FrameworkElement.ToolTipProperty, new Binding(property));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetBinding(TextBlock.ForegroundProperty, MemoryButtonForeground());
        return text;
    }

    private static FrameworkElementFactory BoundBadge(string property, string name, string? icon = null, string? iconProperty = null)
    {
        var badge = new FrameworkElementFactory(typeof(Border)) { Name = name };
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(9)); badge.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        badge.SetValue(Border.PaddingProperty, new Thickness(6, 1, 6, 1)); badge.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
        badge.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        badge.SetValue(FrameworkElement.MaxWidthProperty, 180d);
        badge.SetResourceReference(Border.BackgroundProperty, ThemeBrush.BadgeBackground); badge.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        var content = new FrameworkElementFactory(typeof(DockPanel));
        var glyph = new FrameworkElementFactory(typeof(SqlMemoryIcon));
        if (iconProperty is not null) glyph.SetBinding(SqlMemoryIcon.IconNameProperty, new Binding(iconProperty));
        else glyph.SetValue(SqlMemoryIcon.IconNameProperty, icon ?? "History");
        glyph.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
        glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.AppendChild(glyph);
        var text = BoundText(property); text.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        text.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        content.AppendChild(text); badge.AppendChild(content); return badge;
    }

    public static DataTemplate CreateMemoryMetadataTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.AppendChild(BoundBadge("Status", "state", iconProperty: "StatusIcon"));
        var name = BoundText("Name");
        name.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        name.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground); panel.AppendChild(name);
        panel.AppendChild(BoundBadge("Server", "server", iconProperty: "ServerIcon"));
        panel.AppendChild(BoundBadge("Database", "database", "Database"));
        var time = BoundText("Timestamp"); time.Name = "Timestamp";
        time.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0));
        time.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground); panel.AppendChild(time);
        var template = new DataTemplate { VisualTree = panel };
        var noTime = new DataTrigger { Binding = new Binding("Timestamp"), Value = "" };
        noTime.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "Timestamp")); template.Triggers.Add(noTime);
        var empty = new DataTrigger { Binding = new Binding("Database"), Value = "" };
        empty.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "database")); template.Triggers.Add(empty);
        var executed = new DataTrigger { Binding = new Binding("IsExecuted"), Value = true };
        executed.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, MemoryStatusBackground(true), "state"));
        executed.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, MemoryStatusBorder(true), "state")); template.Triggers.Add(executed);
        return template;
    }
}
