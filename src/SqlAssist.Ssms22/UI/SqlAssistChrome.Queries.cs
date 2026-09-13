using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SqlAssist.Ssms22.UI;

internal static partial class SqlAssistChrome
{
    // 連線沿用中性主題角色；未來 Tab 色擴充只需調整集中於此的 badge。
    internal static ThemeBrush QueryStatusBackground(bool executed) => executed ? ThemeBrush.AccentBackground : ThemeBrush.BadgeBackground;
    internal static ThemeBrush QueryStatusBorder(bool executed) => executed ? ThemeBrush.AccentBorder : ThemeBrush.Hairline;

    public static Geometry QueryIcon(string name) => Geometry.Parse(name switch
    {
        "Search" => "M 10,6 A 4,4 0 1 1 2,6 A 4,4 0 1 1 10,6 M 9,9 L 14,14",
        "Clear" => "M 4,4 L 12,12 M 12,4 L 4,12",
        "Copy" => "M 6,5 L 13,5 13,14 6,14 Z M 10,5 L 10,2 3,2 3,11 6,11",
        "Open" => "M 9,2 L 14,2 14,7 M 14,2 L 7,9 M 6,3 L 2,3 2,14 13,14 13,10",
        "Favorite" => "M 8,1 L 10,5 15,6 11.5,9.5 12.5,15 8,12.5 3.5,15 4.5,9.5 1,6 6,5 Z",
        "Preview" => "M 1,8 Q 8,-2 15,8 Q 8,18 1,8 M 10,8 A 2,2 0 1 1 6,8 A 2,2 0 1 1 10,8",
        "Connection" => "M 8,1 L 8,4 M 8,12 L 8,15 M 1,8 L 4,8 M 12,8 L 15,8 M 13,8 A 5,5 0 1 1 3,8 A 5,5 0 1 1 13,8 M 9,8 A 1,1 0 1 1 7,8 A 1,1 0 1 1 9,8",
        "Chevron" => "M 3,5 L 8,10 13,5",
        "Refresh" => "M 13,6 A 5.5,5.5 0 1 0 13,11 M 13,2 L 13,6 9,6",
        "Settings" => "M 6,1 L 10,1 10.5,3 12,4 14,4 15,7 13.5,8.5 13,10 14,12 11,14 9.5,12.5 7.5,13 6,15 3,13.5 3,11.5 2,10 0.5,9 1.5,6 3.5,5.5 5,4 Z M 10.5,8 A 2.5,2.5 0 1 1 5.5,8 A 2.5,2.5 0 1 1 10.5,8",
        _ => "M 8,2 A 6,6 0 1 1 2,8 M 8,4 L 8,8 11,10"
    });

    public static Path CreateQueryIcon(string name) => new Path
    {
        Data = QueryIcon(name), Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.3,
        VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
    }.WithTheme(Shape.StrokeProperty, ThemeBrush.ListForeground);

    public static Button CreateQueryIconButton(string icon, string label)
    {
        var button = CreateButton("", DefaultMetrics);
        button.Content = CreateQueryButtonIcon(icon); button.ToolTip = label;
        button.Padding = new Thickness(5); button.MinWidth = 26; button.MinHeight = 26;
        AutomationProperties.SetName(button, label);
        return button;
    }

    // Content 的邏輯父層是 Button，不一定繼承樣板 Border 的選取前景；高對比必須讀呈現器。
    private static Binding QueryButtonForeground() => new Binding
    {
        Path = new PropertyPath(TextElement.ForegroundProperty),
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ContentPresenter), 1)
    };

    public static Path CreateQueryButtonIcon(string name)
    {
        var icon = CreateQueryIcon(name); icon.SetBinding(Shape.StrokeProperty, QueryButtonForeground()); return icon;
    }

    public static TextBlock CreateQueryButtonText(string text)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label.SetBinding(TextBlock.ForegroundProperty, QueryButtonForeground()); return label;
    }

    public static Button CreateQueryConnectionButton()
    {
        var button = CreateButton("", DefaultMetrics, true);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(CreateQueryButtonIcon("Connection"));
        var label = CreateQueryButtonText("目前連線"); label.Margin = new Thickness(5, 0, 0, 0); content.Children.Add(label);
        button.Content = content;
        button.ToolTip = "使用目前作用中 SQL 查詢視窗的伺服器與資料庫篩選；不切換連線。";
        AutomationProperties.SetName(button, "以目前連線篩選");
        return button;
    }

    public static Grid CreateQueryToolbar(TabControl tabs, Button connection, Button refresh, Button settings)
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
        foreach (var entry in new[] { (refresh, "Refresh", "重新整理"), (settings, "Settings", "設定") })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(CreateQueryButtonIcon(entry.Item2));
            var label = CreateQueryButtonText(entry.Item3); label.Margin = new Thickness(5, 0, 0, 0);
            labels.Add(label); content.Children.Add(label); entry.Item1.Content = content;
            entry.Item1.ToolTip = entry.Item3; AutomationProperties.SetName(entry.Item1, entry.Item3);
        }
        foreach (var button in new[] { connection, refresh, settings })
        {
            button.Height = 28; button.MinWidth = 28; button.Padding = new Thickness(6, 3, 6, 3);
            button.Margin = new Thickness(4, 0, 0, 0); actions.Children.Add(button);
        }
        // 窄窗只收起次要操作文字，不換行或改變按鈕高度，維持分頁與圖示共用中心線。
        void UpdateLabels() { foreach (var label in labels) label.Visibility = toolbar.ActualWidth >= 400 ? Visibility.Visible : Visibility.Collapsed; }
        toolbar.SizeChanged += (_, _) => UpdateLabels(); UpdateLabels();
        return toolbar;
    }

    public static Border CreateSearchBar(TextBox input, Button clear)
    {
        var panel = new DockPanel();
        var icon = CreateQueryIcon("Search"); icon.Margin = new Thickness(8, 0, 4, 0);
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

    public static Style CreateQueryPillStyle(bool executed = false)
    {
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "pill" };
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.PaddingProperty, new Thickness(8, 3, 8, 3));
        border.SetResourceReference(Border.BackgroundProperty, QueryStatusBackground(executed));
        border.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
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
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 4)));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.ListForeground));
        return style;
    }

    public static Style CreateSqlCardStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "card" };
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
        AddTrigger(template, ListBoxItem.IsSelectedProperty, Border.BackgroundProperty, ThemeBrush.RowSelected, "card");
        AddTrigger(template, ListBoxItem.IsSelectedProperty, TextElement.ForegroundProperty, ThemeBrush.SelectedForeground, "card");
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
        var state = BoundBadge("Status", "state"); state.SetValue(DockPanel.DockProperty, Dock.Left); heading.AppendChild(state);
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
        sql.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); sql.SetValue(TextBlock.LineHeightProperty, 16d);
        sql.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
        sql.SetValue(FrameworkElement.MaxHeightProperty, 32d); code.AppendChild(sql); panel.AppendChild(code);
        var footer = new FrameworkElementFactory(typeof(DockPanel)); panel.AppendChild(footer);
        var actions = new FrameworkElementFactory(typeof(StackPanel)) { Name = "actions" };
        actions.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); actions.SetValue(DockPanel.DockProperty, Dock.Right);
        actions.SetResourceReference(Panel.BackgroundProperty, ThemeBrush.ListBackground);
        // Hidden 保留尺寸，避免懸停時 badge 跳動；鍵盤進入卡片也揭露動作。
        actions.SetValue(UIElement.VisibilityProperty, Visibility.Hidden); footer.AppendChild(actions);
        foreach (var pair in new[] { ("Copy", "複製 SQL"), ("Open", "在新查詢開啟（不執行）"), ("Favorite", "加入收藏"), ("Preview", "預覽") })
        {
            var button = new FrameworkElementFactory(typeof(Button));
            button.SetValue(FrameworkElement.TagProperty, pair.Item1); button.SetValue(FrameworkElement.ToolTipProperty, pair.Item2);
            button.SetValue(AutomationProperties.NameProperty, pair.Item2);
            button.SetValue(Control.TemplateProperty, CreateGhostButtonTemplate()); button.SetValue(Control.PaddingProperty, new Thickness(3));
            button.SetResourceReference(Control.ForegroundProperty, ThemeBrush.ListForeground);
            button.SetValue(FrameworkElement.WidthProperty, 24d); button.SetValue(FrameworkElement.HeightProperty, 22d);
            if (pair.Item1 == "Favorite")
            {
                button.SetBinding(UIElement.IsEnabledProperty, new Binding("CanSave"));
                button.SetBinding(FrameworkElement.ToolTipProperty, new Binding("SaveHint"));
                button.SetValue(ToolTipService.ShowOnDisabledProperty, true);
            }
            var icon = new FrameworkElementFactory(typeof(Path)); icon.SetValue(Path.DataProperty, QueryIcon(pair.Item1));
            icon.SetValue(FrameworkElement.WidthProperty, 14d); icon.SetValue(FrameworkElement.HeightProperty, 14d);
            icon.SetValue(Shape.StretchProperty, Stretch.Uniform); icon.SetValue(Shape.StrokeThicknessProperty, 1.3);
            icon.SetBinding(Shape.StrokeProperty, QueryButtonForeground());
            button.AppendChild(icon); actions.AppendChild(button);
        }
        var connections = new FrameworkElementFactory(typeof(WrapPanel)); footer.AppendChild(connections);
        connections.AppendChild(BoundBadge("Server", "server")); connections.AppendChild(BoundBadge("Database", "database"));
        var template = new DataTemplate { VisualTree = panel };
        var noDatabase = new DataTrigger { Binding = new Binding("Database"), Value = "" };
        noDatabase.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "database")); template.Triggers.Add(noDatabase);
        var executed = new DataTrigger { Binding = new Binding("IsExecuted"), Value = true };
        executed.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, QueryStatusBackground(true), "state"));
        executed.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, QueryStatusBorder(true), "state")); template.Triggers.Add(executed);
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
        return text;
    }

    private static FrameworkElementFactory BoundBadge(string property, string name)
    {
        var badge = new FrameworkElementFactory(typeof(Border)) { Name = name };
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(9)); badge.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        badge.SetValue(Border.PaddingProperty, new Thickness(6, 1, 6, 2)); badge.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
        badge.SetValue(FrameworkElement.MaxWidthProperty, 180d);
        badge.SetResourceReference(Border.BackgroundProperty, ThemeBrush.BadgeBackground); badge.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        var text = BoundText(property); text.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        text.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground); badge.AppendChild(text); return badge;
    }
}
