using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SqlAssist.Ssms22.UI;

internal static partial class SqlAssistChrome
{
    /// <summary>SQL 主從清單的三層摘要；資料只需提供 Name、Preview、Detail。</summary>
    public static DataTemplate CreateSqlSummaryTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 4));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        title.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Name"));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        panel.AppendChild(title);
        var sql = new FrameworkElementFactory(typeof(TextBlock));
        sql.SetBinding(TextBlock.TextProperty, new Binding("Preview"));
        sql.SetValue(TextBlock.FontFamilyProperty, CodeFont);
        sql.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        sql.SetValue(TextBlock.LineHeightProperty, 18d);
        sql.SetValue(FrameworkElement.MaxHeightProperty, 36d);
        sql.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 4));
        panel.AppendChild(sql);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new Binding("Detail"));
        detail.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Detail"));
        detail.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        detail.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        detail.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        panel.AppendChild(detail);
        return new DataTemplate { VisualTree = panel };
    }
}
