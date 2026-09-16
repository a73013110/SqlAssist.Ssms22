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
    private static readonly Geometry ChevronGeometry = Frozen(Geometry.Parse("M 2,5 L 8,11 14,5"));

    // 語意值決定圖示，不依賴可翻譯的顯示文字；篩選、卡片與 Preview 都查同一份對應。
    // 沒有對應就擲出，不退回某一顆看似合理的圖示把漏掉的選項藏起來。
    public static SqlIcon MemoryOptionIcon(object value) => value switch
    {
        SqlHistoryFilter.Executions => SqlIcon.Execute, SqlHistoryFilter.Drafts => SqlIcon.Edit, SqlHistoryFilter.All => SqlIcon.All,
        SqlHistoryPeriod.Any => SqlIcon.AnyTime, SqlHistoryPeriod => SqlIcon.Calendar,
        SqlFavoriteScope.Server => SqlIcon.Server, SqlFavoriteScope.Database => SqlIcon.Database, SqlFavoriteScope.Global => SqlIcon.Global,
        SqlConnectionFacetSort.Recent or SqlConnectionFacetSort.ReverseAlphabetical => SqlIcon.SortDescending,
        SqlConnectionFacetSort.Oldest or SqlConnectionFacetSort.Alphabetical => SqlIcon.SortAscending,
        _ => throw new System.ArgumentOutOfRangeException(nameof(value), value, "這個選項值沒有對應的圖示。")
    };
    // 狀態不能只靠顏色，卡片仍保留文字標籤。
    internal static ThemeBrush MemoryStatusBackground(bool executed) => executed ? ThemeBrush.AccentBackground : ThemeBrush.BadgeBackground;
    internal static ThemeBrush MemoryStatusBorder(bool executed) => executed ? ThemeBrush.AccentBorder : ThemeBrush.Hairline;

    private static Geometry Frozen(Geometry geometry) { geometry.Freeze(); return geometry; }

    public static SqlIconImage CreateIcon(SqlIcon icon) => new() { Icon = icon };

    /// <summary>展開／收合箭頭；屬於控制項外觀而非語意圖示，所以畫向量並跟隨所屬控制項前景。</summary>
    /// <remarks>旋轉只改繪圖、不改量測；幾何以 16 DIP 畫布中心對稱，收合時不推動旁邊文字。</remarks>
    public static Path CreateChevron(bool expanded = true)
    {
        var chevron = new Path
        {
            Data = ChevronGeometry, Width = 16, Height = 16, Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, StrokeThickness = 1.3,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(expanded ? 0 : -90)
        };
        chevron.SetBinding(Shape.StrokeProperty, MemoryButtonForeground());
        return chevron;
    }

    public static Button CreateIconButton(SqlIcon icon, string label, SqlActionTone tone = SqlActionTone.Neutral)
    {
        var button = CreateButton("", DefaultMetrics);
        button.Content = CreateIcon(icon); button.ToolTip = label;
        button.Padding = new Thickness(5); button.MinWidth = 26; button.MinHeight = 26;
        if (tone != SqlActionTone.Neutral) button.Template = CreateGhostButtonTemplate(tone);
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

    public static TextBlock CreateMemoryButtonText(string text)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetBinding(TextBlock.ForegroundProperty, MemoryButtonForeground()); return label;
    }

    public static DockPanel CreateMemoryLabel(SqlIcon icon, string text)
    {
        var content = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        var glyph = CreateIcon(icon); glyph.Margin = new Thickness(0, 0, 5, 0);
        DockPanel.SetDock(glyph, Dock.Left); content.Children.Add(glyph);
        content.Children.Add(CreateMemoryButtonText(text));
        return content;
    }

    public static TabItem CreateMemoryTab(SqlIcon icon, string label)
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
        button.Content = CreateMemoryLabel(SqlIcon.Connection, "目前連線");
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
        foreach (var entry in new[] { (refresh, SqlIcon.Refresh, "重新整理"), (settings, SqlIcon.Settings, "設定") })
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
        var icon = CreateIcon(SqlIcon.Search); icon.Margin = new Thickness(8, 0, 4, 0);
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

    /// <param name="motion">null 讀全域動畫設定；測試明確指定，不受執行環境的 Windows 動畫偏好左右。</param>
    public static Style CreateSqlCardStyle(bool? motion = null)
    {
        var animate = motion ?? MotionEnabled;
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
        if (animate)
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
        if (animate) AddMemoryCardMotion(border, template);
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

    /// <summary>新列淡入上移、被刪除的列淡出；揭露動畫只給列本身，不改卡片量測。</summary>
    internal static readonly System.TimeSpan MemoryCardEnterDuration = System.TimeSpan.FromMilliseconds(180);

    /// <summary>刪除列淡出的時間；清單等它結束才真正移除，動畫關閉時立即移除。</summary>
    internal static readonly System.TimeSpan MemoryCardExitDuration = System.TimeSpan.FromMilliseconds(140);

    /// <remarks>
    /// 以列資料的 <c>IsNew</c>／<c>IsRemoving</c> 觸發，而不是容器的 Loaded：清單是 recycling 虛擬化，
    /// 捲動時重用的容器每次都會 Loaded，綁在那裡就會一路重播。位移走 RenderTransform，不推動其他列。
    /// </remarks>
    private static void AddMemoryCardMotion(FrameworkElementFactory card, ControlTemplate template)
    {
        card.SetValue(UIElement.RenderTransformProperty, new TranslateTransform());
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        Storyboard Motion(double? fromOpacity, double toOpacity, double? fromY, double toY, System.TimeSpan duration, FillBehavior fill)
        {
            var storyboard = new Storyboard { FillBehavior = fill };
            var fade = new DoubleAnimation { From = fromOpacity, To = toOpacity, Duration = duration, EasingFunction = ease };
            Storyboard.SetTargetName(fade, "card"); Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
            var slide = new DoubleAnimation { From = fromY, To = toY, Duration = duration, EasingFunction = ease };
            Storyboard.SetTargetName(slide, "card");
            Storyboard.SetTargetProperty(slide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
            storyboard.Children.Add(fade); storyboard.Children.Add(slide);
            return storyboard;
        }

        // 進場結束就回到基底值（不透明、無位移），之後的 hover／selected 不受保留值影響。
        var enter = new DataTrigger { Binding = new Binding("IsNew"), Value = true };
        enter.EnterActions.Add(new BeginStoryboard { Storyboard = Motion(0, 1, 6, 0, MemoryCardEnterDuration, FillBehavior.Stop) });
        template.Triggers.Add(enter);
        // 退場保持結束值直到列被移除；可中途反向：刪除失敗或容器被回收給別的列時，從當下值回到基底。
        var exit = new DataTrigger { Binding = new Binding("IsRemoving"), Value = true };
        exit.EnterActions.Add(new BeginStoryboard { Storyboard = Motion(null, 0, null, -4, MemoryCardExitDuration, FillBehavior.HoldEnd) });
        exit.ExitActions.Add(new BeginStoryboard { Storyboard = Motion(null, 1, null, 0, MemoryCardExitDuration, FillBehavior.Stop) });
        template.Triggers.Add(exit);
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
        var favorite = new DataTrigger { Binding = new Binding("IsFavorite"), Value = true };
        var history = new DataTrigger { Binding = new Binding("IsFavorite"), Value = false };
        foreach (var command in SqlMemoryRowCommand.All)
        {
            var button = new FrameworkElementFactory(typeof(Button)) { Name = "action" + command.Action };
            button.SetValue(FrameworkElement.TagProperty, command.Action); button.SetValue(FrameworkElement.ToolTipProperty, command.Label);
            button.SetValue(AutomationProperties.NameProperty, command.Label);
            button.SetValue(Control.TemplateProperty, CreateGhostButtonTemplate(command.Tone)); button.SetValue(Control.PaddingProperty, new Thickness(3));
            // 動作列不再有實色底，前景必須跟隨卡片的 hover／selected 配對色（尤其高對比）。
            button.SetBinding(Control.ForegroundProperty, MemoryButtonForeground());
            button.SetValue(FrameworkElement.WidthProperty, 24d); button.SetValue(FrameworkElement.HeightProperty, 22d);
            if (command.IsSeparated) button.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
            if (command.LabelProperty is { } labelProperty)
            {
                button.SetBinding(FrameworkElement.ToolTipProperty, new Binding(labelProperty));
                button.SetBinding(AutomationProperties.NameProperty, new Binding(labelProperty));
            }
            // 不適用的操作直接收起，不留停用的灰色按鈕；判斷來源與快捷選單、Preview 相同。
            if (command.Kind == SqlMemoryRowKind.History) favorite.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, button.Name));
            else if (command.Kind == SqlMemoryRowKind.Favorite) history.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, button.Name));
            var icon = new FrameworkElementFactory(typeof(SqlIconImage)); icon.SetValue(SqlIconImage.IconProperty, command.Icon);
            button.AppendChild(icon); actions.AppendChild(button);
        }
        var connections = new FrameworkElementFactory(typeof(WrapPanel)); footer.AppendChild(connections);
        connections.AppendChild(BoundBadge("Server", "server", iconProperty: "ServerIcon")); connections.AppendChild(BoundBadge("Database", "database", SqlIcon.Database));
        var template = new DataTemplate { VisualTree = panel };
        template.Triggers.Add(favorite); template.Triggers.Add(history);
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

    private static FrameworkElementFactory BoundBadge(string property, string name, SqlIcon? icon = null, string? iconProperty = null)
    {
        var badge = new FrameworkElementFactory(typeof(Border)) { Name = name };
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(9)); badge.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        badge.SetValue(Border.PaddingProperty, new Thickness(6, 1, 6, 1)); badge.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
        badge.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        badge.SetValue(FrameworkElement.MaxWidthProperty, 180d);
        badge.SetResourceReference(Border.BackgroundProperty, ThemeBrush.BadgeBackground); badge.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        var content = new FrameworkElementFactory(typeof(DockPanel));
        var glyph = new FrameworkElementFactory(typeof(SqlIconImage));
        if (iconProperty is not null) glyph.SetBinding(SqlIconImage.IconProperty, new Binding(iconProperty));
        else glyph.SetValue(SqlIconImage.IconProperty, icon);
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
        // 膠囊全部排在前面，檔名與時間緊接在後：文字不再被夾在兩組膠囊中間，資訊列一眼讀得完。
        panel.AppendChild(BoundBadge("Status", "state", iconProperty: "StatusIcon"));
        panel.AppendChild(BoundBadge("Server", "server", iconProperty: "ServerIcon"));
        panel.AppendChild(BoundBadge("Database", "database", SqlIcon.Database));
        var name = BoundText("Name");
        name.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 8, 0));
        name.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground); panel.AppendChild(name);
        var time = BoundText("Timestamp"); time.Name = "Timestamp";
        time.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
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
