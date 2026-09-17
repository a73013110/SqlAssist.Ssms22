using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.UI;

/// <summary>收藏版本時間軸與行級差異的樣板；配色只用語意資源鍵，版面與 SQL Memory 卡片同一套節奏。</summary>
internal static partial class SqlAssistChrome
{
    /// <summary>時間軸圓點中心到列頂的距離；上下兩段軌道在這裡接上圓點。</summary>
    private const double RevisionDotCenter = 14;

    /// <summary>
    /// 時間軸的列容器：左側軌道與圓點、右側可選取的內容表面。
    /// </summary>
    /// <remarks>
    /// 列之間不留外距，軌道才連成一條線；第一列不向上延伸、已知的最後一列不向下延伸。
    /// 目前版本是實心強調色圓點，其他版本是空心圓點——除了顏色還有形狀可辨識。
    /// 新列的進場與 SQL Memory 卡片共用同一組揭露動畫，走 RenderTransform 不推動其他列。
    /// </remarks>
    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    public static Style CreateRevisionItemStyle(bool? motion = null)
    {
        var root = new FrameworkElementFactory(typeof(Grid)) { Name = "card" };
        root.SetBinding(TextElement.ForegroundProperty, TemplatedParent(nameof(Control.Foreground)));
        // 軌道固定 20 DIP 寬貼齊左緣，內容以左外距讓開；不用欄定義，樣板工廠只建立子元素。
        var rail = new FrameworkElementFactory(typeof(Grid));
        rail.SetValue(FrameworkElement.WidthProperty, 20d);
        rail.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        root.AppendChild(rail);

        FrameworkElementFactory Rail(string name, bool top)
        {
            var line = new FrameworkElementFactory(typeof(Border)) { Name = name };
            line.SetValue(FrameworkElement.WidthProperty, 1d);
            line.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            line.SetValue(FrameworkElement.VerticalAlignmentProperty, top ? VerticalAlignment.Top : VerticalAlignment.Stretch);
            if (top) line.SetValue(FrameworkElement.HeightProperty, RevisionDotCenter);
            else line.SetValue(FrameworkElement.MarginProperty, new Thickness(0, RevisionDotCenter, 0, 0));
            line.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            line.SetResourceReference(Border.BackgroundProperty, ThemeBrush.Hairline);
            return line;
        }
        rail.AppendChild(Rail("railTop", true));
        rail.AppendChild(Rail("railBottom", false));
        var dot = new FrameworkElementFactory(typeof(Border)) { Name = "dot" };
        dot.SetValue(FrameworkElement.WidthProperty, 9d); dot.SetValue(FrameworkElement.HeightProperty, 9d);
        dot.SetValue(Border.CornerRadiusProperty, new CornerRadius(4.5));
        dot.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        dot.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
        dot.SetValue(FrameworkElement.MarginProperty, new Thickness(0, RevisionDotCenter - 4.5, 0, 0));
        dot.SetResourceReference(Border.BackgroundProperty, ThemeBrush.WindowBackground);
        dot.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.DimForeground);
        rail.AppendChild(dot);

        var surface = new FrameworkElementFactory(typeof(Border)) { Name = "surface" };
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        surface.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        surface.SetValue(Border.PaddingProperty, new Thickness(8, 4, 8, 5));
        surface.SetValue(FrameworkElement.MarginProperty, new Thickness(20, 0, 2, 2));
        surface.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        surface.SetValue(Border.BorderBrushProperty, System.Windows.Media.Brushes.Transparent);
        surface.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        root.AppendChild(surface);

        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = root };
        DataTriggerSetter(template, "IsCurrent", Border.BackgroundProperty, ThemeBrush.AccentBorder, "dot");
        DataTriggerSetter(template, "IsCurrent", Border.BorderBrushProperty, ThemeBrush.AccentBorder, "dot");
        foreach (var (flag, segment) in new[] { ("IsFirst", "railTop"), ("IsLast", "railBottom") })
        {
            var hide = new DataTrigger { Binding = new Binding(flag), Value = true };
            hide.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Hidden, segment));
            template.Triggers.Add(hide);
        }
        AddTrigger(template, UIElement.IsMouseOverProperty, Border.BackgroundProperty, ThemeBrush.RowHover, "surface");
        AddTrigger(template, UIElement.IsMouseOverProperty, Control.ForegroundProperty, ThemeBrush.SelectedForeground);
        AddTrigger(template, ListBoxItem.IsSelectedProperty, Border.BackgroundProperty, ThemeBrush.RowSelected, "surface");
        AddTrigger(template, ListBoxItem.IsSelectedProperty, Border.BorderBrushProperty, ThemeBrush.AccentBorder, "surface");
        AddTrigger(template, ListBoxItem.IsSelectedProperty, Control.ForegroundProperty, ThemeBrush.SelectedForeground);
        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty, Border.BorderBrushProperty, ThemeBrush.AccentBorder, "surface");
        if (motion ?? MotionEnabled) AddMemoryCardMotion(root, template, removable: false);

        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, InterfaceFont));
        style.Setters.Add(new Setter(Control.FontSizeProperty, DefaultMetrics.Body));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.ListForeground));
        return style;
    }

    /// <summary>
    /// 時間軸列的內容：時間與目前版本標示、單行 SQL 摘要、來源與長度；操作在停駐或鍵盤進入時才揭露。
    /// </summary>
    public static DataTemplate CreateRevisionItemTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        var heading = new FrameworkElementFactory(typeof(DockPanel)); panel.AppendChild(heading);
        var current = new FrameworkElementFactory(typeof(Border)) { Name = "current" };
        current.SetValue(DockPanel.DockProperty, Dock.Left);
        current.SetValue(Border.CornerRadiusProperty, new CornerRadius(9)); current.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        current.SetValue(Border.PaddingProperty, new Thickness(6, 0, 6, 1)); current.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        current.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        current.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        current.SetResourceReference(Border.BackgroundProperty, ThemeBrush.AccentBackground);
        current.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.AccentBorder);
        var currentText = new FrameworkElementFactory(typeof(TextBlock));
        currentText.SetValue(TextBlock.TextProperty, "目前版本"); currentText.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        currentText.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        current.AppendChild(currentText); heading.AppendChild(current);

        var actions = new FrameworkElementFactory(typeof(StackPanel)) { Name = "actions" };
        actions.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); actions.SetValue(DockPanel.DockProperty, Dock.Right);
        // Hidden 保留尺寸：停駐揭露操作時時間文字不跳動。
        actions.SetValue(UIElement.VisibilityProperty, Visibility.Hidden); heading.AppendChild(actions);
        var template = new DataTemplate { VisualTree = panel };
        foreach (var command in SqlFavoriteRevisionCommand.All)
        {
            var button = CreateRowActionButton("action" + command.Action, command.Action, command.Icon, command.Label, command.Tone, command.IsSeparated);
            if (command.LabelProperty is { } labelProperty)
            {
                button.SetBinding(FrameworkElement.ToolTipProperty, new Binding(labelProperty));
                button.SetBinding(AutomationProperties.NameProperty, new Binding(labelProperty));
                button.SetValue(ToolTipService.ShowOnDisabledProperty, true);
            }
            if (command.Action == SqlFavoriteRevisionAction.Revert) button.SetBinding(UIElement.IsEnabledProperty, new Binding("CanRevert"));
            if (command.HiddenOnCurrent)
            {
                var hide = new DataTrigger { Binding = new Binding("IsCurrent"), Value = true };
                hide.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, button.Name));
                template.Triggers.Add(hide);
            }
            actions.AppendChild(button);
        }

        var time = BoundText("RelativeTime"); time.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        time.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Timestamp")); heading.AppendChild(time);

        var code = new FrameworkElementFactory(typeof(Border)); code.SetResourceReference(Border.BackgroundProperty, ThemeBrush.RowAlternate);
        code.SetValue(Border.CornerRadiusProperty, new CornerRadius(4)); code.SetValue(Border.PaddingProperty, new Thickness(6, 2, 6, 2));
        code.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 3));
        var sql = BoundText("Preview"); sql.SetValue(TextBlock.FontFamilyProperty, CodeFont);
        sql.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap); sql.SetValue(FrameworkElement.MaxHeightProperty, 16d);
        sql.SetValue(TextBlock.LineHeightProperty, 16d); sql.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
        code.AppendChild(sql); panel.AppendChild(code);

        var detail = BoundText("Detail"); detail.Name = "detail";
        detail.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        detail.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        panel.AppendChild(detail);

        var isCurrent = new DataTrigger { Binding = new Binding("IsCurrent"), Value = true };
        isCurrent.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "current"));
        template.Triggers.Add(isCurrent);
        foreach (var property in new[] { "IsMouseOver", "IsKeyboardFocusWithin", "IsSelected" })
        {
            var reveal = new DataTrigger
            {
                Binding = new Binding(property) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) },
                Value = true
            };
            reveal.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "actions"));
            if (property != "IsKeyboardFocusWithin")
                reveal.Setters.Add(ThemeResourceSet.Setter(TextBlock.ForegroundProperty, ThemeBrush.SelectedForeground, "detail"));
            template.Triggers.Add(reveal);
        }
        return template;
    }

    /// <summary>
    /// 一行差異：+／- 標記、舊／新行號與原文。新增與刪除以語意底色鋪滿整行，標記與行號用配對前景。
    /// </summary>
    /// <remarks>高對比不上底色；標記本身就是非顏色的辨識，行號欄也只在對應的一側有值。</remarks>
    public static DataTemplate CreateDiffLineTemplate()
    {
        var line = new FrameworkElementFactory(typeof(DockPanel)) { Name = "line" };
        line.SetValue(Panel.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        line.SetValue(DockPanel.LastChildFillProperty, true);

        FrameworkElementFactory Cell(int column, string? path, string name, TextAlignment alignment, double minWidth)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock)) { Name = name };
            if (column < 3) text.SetValue(DockPanel.DockProperty, Dock.Left);
            if (path is not null) text.SetBinding(TextBlock.TextProperty, new Binding(path));
            text.SetValue(TextBlock.TextAlignmentProperty, alignment);
            text.SetValue(FrameworkElement.MinWidthProperty, minWidth);
            text.SetValue(TextBlock.PaddingProperty, new Thickness(4, 0, 6, 0));
            text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            text.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
            line.AppendChild(text);
            return text;
        }

        var marker = Cell(0, null, "marker", TextAlignment.Center, 20);
        marker.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        marker.SetValue(TextBlock.PaddingProperty, new Thickness(0));
        marker.SetValue(AutomationProperties.NameProperty, "未變更");
        Cell(1, nameof(SqlTextDiffLine.OldNumber), "oldNumber", TextAlignment.Right, 40);
        Cell(2, nameof(SqlTextDiffLine.NewNumber), "newNumber", TextAlignment.Right, 40);
        var text = Cell(3, nameof(SqlTextDiffLine.Text), "text", TextAlignment.Left, 0);
        text.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        text.SetValue(TextBlock.PaddingProperty, new Thickness(6, 0, 12, 0));

        var template = new DataTemplate { VisualTree = line };
        foreach (var (kind, symbol, name, tint, paired) in new[]
                 {
                     (SqlTextDiffLineKind.Added, "+", "新增", ThemeBrush.DiffAddedBackground, ThemeBrush.DiffAddedForeground),
                     (SqlTextDiffLineKind.Removed, "-", "刪除", ThemeBrush.DiffRemovedBackground, ThemeBrush.DiffRemovedForeground),
                 })
        {
            var trigger = new DataTrigger { Binding = new Binding(nameof(SqlTextDiffLine.Kind)), Value = kind };
            trigger.Setters.Add(ThemeResourceSet.Setter(Panel.BackgroundProperty, tint, "line"));
            trigger.Setters.Add(new Setter(TextBlock.TextProperty, symbol, "marker"));
            trigger.Setters.Add(new Setter(AutomationProperties.NameProperty, name, "marker"));
            foreach (var cell in new[] { "marker", "oldNumber", "newNumber" })
                trigger.Setters.Add(ThemeResourceSet.Setter(TextBlock.ForegroundProperty, paired, cell));
            template.Triggers.Add(trigger);
        }
        return template;
    }

    private static void DataTriggerSetter(ControlTemplate template, string path, DependencyProperty property, ThemeBrush brush, string target)
    {
        var trigger = new DataTrigger { Binding = new Binding(path), Value = true };
        trigger.Setters.Add(ThemeResourceSet.Setter(property, brush, target));
        template.Triggers.Add(trigger);
    }

    /// <summary>無外觀的列容器：差異行只是閱讀內容，不參與選取，也不畫選取底色。</summary>
    internal static Style CreatePlainItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty,
            new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }));
        style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }
}
