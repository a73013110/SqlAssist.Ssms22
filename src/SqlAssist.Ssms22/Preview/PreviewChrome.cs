using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SqlAssist.Metadata;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 浮動預覽的外觀。
/// </summary>
/// <remarks>
/// 樣板全部用 <see cref="FrameworkElementFactory"/> 在程式碼裡組出來，與這個專案
/// 其餘的 WPF 一致——整份原始碼沒有任何 XAML，為了幾個樣板引入資源字典會讓
/// 顏色的來源分裂成兩套：字典裡只能用 <c>DynamicResource</c> 查主題鍵，
/// 那條路沒有備援，SSMS 還沒併入主題字典時會直接解析成透明。
/// 顏色一律從 <see cref="VsThemeBrushes"/> 取，備援才有地方寫。
///
/// 版面的原則是「用留白分層，不用線條」：整個視窗只剩兩條細線（欄位標題下緣
/// 與外框），其餘層次都靠間距與極淡的底色。線條畫得愈少，一百多列的表格
/// 看起來愈安靜。
///
/// 只在真正需要時才換掉整個 <see cref="ControlTemplate"/>。換掉樣板等於連同
/// 內建的互動元件一起丟掉——欄位標題裡藏著調整寬度的握把，儲存格的樣板則
/// 綁著資料格自己的捲動行為。能用設定式與觸發程序達成的就不動樣板，
/// 只有分頁列非換不可，因為分段控制器的形狀本來就不是分頁。
/// </remarks>
internal static class PreviewChrome
{
    /// <summary>介面字型；沒有 Variable 字族的機器會退回 Segoe UI。</summary>
    public static readonly FontFamily InterfaceFont = new("Segoe UI Variable Text, Segoe UI");

    /// <summary>與 DWM 圓角搭配的內圓角，比外框小一級才不會看起來腫。</summary>
    private const double InnerRadius = 5;

    /// <summary>主索引鍵徽章要換成強調色，比對的就是這個字串。</summary>
    public const string PrimaryKeyFlag = "PK";

    /// <summary>
    /// 從基準字級推導出來的一整組字級與行高。
    /// </summary>
    /// <remarks>
    /// 使用者只調一個數字，其餘六個按固定的差距跟著走。讓他自己維持
    /// 「標題比內文大一號、徽章比欄位標題再小一點」這種比例，
    /// 是把版面設計的工作丟給使用者。
    /// </remarks>
    public readonly struct Metrics
    {
        public Metrics(double baseSize)
        {
            Body = baseSize;
            Title = baseSize + 1;
            Caption = baseSize - 1;
            ColumnHeader = baseSize - 1.5;
            Badge = baseSize - 2.5;

            // 行高跟著字級走，否則字放大了行距沒放大，一列一列就黏在一起。
            RowHeight = Math.Round(baseSize + 11);
        }

        /// <summary>資料格與分頁標籤。</summary>
        public double Body { get; }

        /// <summary>物件名稱。</summary>
        public double Title { get; }

        /// <summary>摘要與底部訊息。</summary>
        public double Caption { get; }

        public double ColumnHeader { get; }

        public double Badge { get; }

        public double RowHeight { get; }
    }

    /// <summary>
    /// 出現時的淡入。
    /// </summary>
    /// <remarks>
    /// 只做透明度，不做縮放。承載視窗的大小是平台按內容量出來的，縮放只會讓
    /// 內容縮進去而視窗不動，四周露出一圈承載視窗的底色——那比不做動畫還糟。
    /// 120 毫秒、只用 ease-out、不回彈：短到不擋操作，但足以讓人看出它是
    /// 「長出來」而不是「跳出來」。
    /// </remarks>
    public static void PlayAppear(UIElement element)
    {
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>
    /// 物件種類的圖示。
    /// </summary>
    /// <remarks>
    /// 標題原本是「Table　[dbo].[PUBLISHER]」這樣一整串同色的字。種類換成圖示、
    /// 結構描述壓成淡色之後，資訊量沒有變，但一眼要讀的字少了一半。
    /// </remarks>
    public static Path CreateObjectIcon()
    {
        return new Path
        {
            Width = 16,
            Height = 16,
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stroke = VsThemeBrushes.DimForeground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Data = GeometryFor(SqlObjectKind.Unknown)
        };
    }

    /// <summary>各種物件的圖示外框；都是純線條沒有填色，深淺主題都成立。</summary>
    public static Geometry GeometryFor(SqlObjectKind kind)
    {
        var data = kind switch
        {
            SqlObjectKind.Table => "M2.5,3.5 H13.5 V12.5 H2.5 Z M2.5,6.5 H13.5 M6,6.5 V12.5",
            SqlObjectKind.View => "M2,8 C4.5,4.5 11.5,4.5 14,8 C11.5,11.5 4.5,11.5 2,8 Z",
            SqlObjectKind.Procedure => "M6,3.5 L3,8 L6,12.5 M10,3.5 L13,8 L10,12.5",
            SqlObjectKind.ScalarFunction or
                SqlObjectKind.InlineTableFunction or
                SqlObjectKind.TableValuedFunction =>
                "M5,12.5 V5.5 A2,2 0 0,1 9,5.5 M3.5,8 H8.5 M10.5,8 H13.5",
            SqlObjectKind.Synonym => "M3,8 H10.5 M8,5.5 L10.5,8 L8,10.5 M13,4 V12",
            _ => "M8,3.5 A4.5,4.5 0 1,0 8,12.5 A4.5,4.5 0 1,0 8,3.5"
        };

        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// 沒有邊框的按鈕，滑鼠移上去才長出底色。
    /// </summary>
    /// <remarks>
    /// 標題列本來有兩個帶邊框的方鈕，兩條額外的線加在一張只有 620 寬的卡片上很吵。
    /// 平常只留文字，需要按的時候才提示可按——與「用留白分層」是同一條原則。
    /// </remarks>
    public static ControlTemplate CreateGhostButtonTemplate()
    {
        var background = new FrameworkElementFactory(typeof(Border)) { Name = "bg" };
        background.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        background.SetValue(Border.CornerRadiusProperty, new CornerRadius(InnerRadius));
        background.SetBinding(Border.PaddingProperty, TemplatedParent(nameof(Control.Padding)));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        background.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = background };

        AddTrigger(
            template, UIElement.IsMouseOverProperty,
            Border.BackgroundProperty, VsThemeBrushes.RowHover, "bg");

        AddTrigger(
            template, ButtonBase.IsPressedProperty,
            Border.BackgroundProperty, VsThemeBrushes.RowSelected, "bg");

        return template;
    }

    /// <summary>
    /// 把分頁列變成分段控制器：一條圓角底槽，選到的那一段浮起來。
    /// </summary>
    /// <remarks>
    /// 內建的分頁樣式會替每一個分頁畫一個方框，五個分頁就是五組線條。
    /// 分段控制器只有一條底槽，選取靠「浮起來的那一段」而不是外框，
    /// 一眼看得出選中誰，畫出來的線卻少了五倍。
    ///
    /// 版面用 <see cref="DockPanel"/> 而不是 <see cref="Grid"/>：
    /// <see cref="FrameworkElementFactory"/> 沒辦法宣告資料列定義，
    /// 而「頂端一條、其餘填滿」本來就是停駐面板在做的事。
    /// </remarks>
    public static ControlTemplate CreateTabControlTemplate()
    {
        var layout = new FrameworkElementFactory(typeof(DockPanel));
        layout.SetValue(DockPanel.LastChildFillProperty, true);

        var track = new FrameworkElementFactory(typeof(Border));
        track.SetValue(DockPanel.DockProperty, Dock.Top);
        track.SetValue(Border.BackgroundProperty, VsThemeBrushes.SegmentTrack);
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        track.SetValue(Border.PaddingProperty, new Thickness(2));
        track.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        track.SetValue(FrameworkElement.MarginProperty, new Thickness(14, 0, 14, 10));

        var headers = new FrameworkElementFactory(typeof(TabPanel));
        headers.SetValue(Panel.IsItemsHostProperty, true);
        track.AppendChild(headers);

        // ContentSource 不是相依性屬性，在這裡設不了；直接把 Content 綁到
        // 分頁控制項選到的那一份內容，效果一樣。
        var body = new FrameworkElementFactory(typeof(ContentPresenter));
        body.SetBinding(ContentPresenter.ContentProperty, TemplatedParent(nameof(TabControl.SelectedContent)));

        layout.AppendChild(track);
        layout.AppendChild(body);

        return new ControlTemplate(typeof(TabControl)) { VisualTree = layout };
    }

    /// <summary>分段控制器裡的一段。</summary>
    public static ControlTemplate CreateTabItemTemplate()
    {
        var segment = new FrameworkElementFactory(typeof(Border)) { Name = "segment" };
        segment.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        segment.SetValue(Border.CornerRadiusProperty, new CornerRadius(InnerRadius));
        segment.SetValue(Border.PaddingProperty, new Thickness(12, 3, 12, 4));

        var label = new FrameworkElementFactory(typeof(ContentPresenter)) { Name = "label" };
        label.SetBinding(ContentPresenter.ContentProperty, TemplatedParent(nameof(TabItem.Header)));
        label.SetValue(TextElement.ForegroundProperty, VsThemeBrushes.DimForeground);
        label.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        segment.AppendChild(label);

        var template = new ControlTemplate(typeof(TabItem)) { VisualTree = segment };

        // 滑鼠掃過只把字提亮，不加底色——底色是「被選中」的專屬訊號。
        AddTrigger(
            template, UIElement.IsMouseOverProperty,
            TextElement.ForegroundProperty, VsThemeBrushes.ListForeground, "label");

        var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, VsThemeBrushes.ListBackground, "segment"));
        selected.Setters.Add(new Setter(TextElement.ForegroundProperty, VsThemeBrushes.ListForeground, "label"));
        template.Triggers.Add(selected);

        return template;
    }

    /// <summary>欄位標題：一條細線把它跟資料分開，字比資料更小也更淡。</summary>
    public static Style CreateColumnHeaderStyle(Metrics metrics)
    {
        var style = new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, VsThemeBrushes.Hairline));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, VsThemeBrushes.DimForeground));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, InterfaceFont));
        style.Setters.Add(new Setter(Control.FontSizeProperty, metrics.ColumnHeader));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 0, 10, 0)));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, metrics.RowHeight + 2));
        style.Setters.Add(new Setter(
            Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        return style;
    }

    /// <summary>儲存格：只有選取才換底色，沒有焦點框也沒有格線。</summary>
    public static Style CreateCellStyle()
    {
        var style = new Style(typeof(DataGridCell));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, default(Thickness)));

        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, VsThemeBrushes.RowSelected));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, VsThemeBrushes.ListForeground));
        style.Triggers.Add(selected);

        return style;
    }

    /// <summary>資料格裡的文字：垂直置中，左右留出與標題一致的內距。</summary>
    public static Style CreateCellTextStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(10, 0, 10, 0)));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        return style;
    }

    /// <summary>
    /// 一列旗標徽章。
    /// </summary>
    /// <remarks>
    /// NULL、PK、IDENTITY 原本是三個獨立的文字欄，每一列都要為那三欄留寬度，
    /// 而絕大多數格子是空的。收成一欄膠囊之後欄位表從八欄變成六欄，
    /// 空的地方就真的什麼都不畫。
    ///
    /// 只標例外：可為 NULL 是 SQL 的預設，因此不給徽章——沒有徽章就是可為 NULL。
    /// </remarks>
    public static DataTemplate CreateFlagsCellTemplate(string path, Metrics metrics)
    {
        var chip = new FrameworkElementFactory(typeof(Border)) { Name = "chip" };
        chip.SetValue(Border.BackgroundProperty, VsThemeBrushes.BadgeBackground);
        chip.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        chip.SetValue(Border.PaddingProperty, new Thickness(6, 0, 6, 1));
        chip.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
        chip.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var text = new FrameworkElementFactory(typeof(TextBlock)) { Name = "chipText" };
        text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetValue(TextBlock.FontFamilyProperty, InterfaceFont);
        text.SetValue(TextBlock.FontSizeProperty, metrics.Badge);
        text.SetValue(TextBlock.ForegroundProperty, VsThemeBrushes.DimForeground);
        chip.AppendChild(text);

        var chipTemplate = new DataTemplate { VisualTree = chip };

        // 整個視窗只有主索引鍵用強調色。多給一個，強調就不再是強調。
        var primaryKey = new DataTrigger { Binding = new Binding(), Value = PrimaryKeyFlag };
        primaryKey.Setters.Add(new Setter(Border.BackgroundProperty, VsThemeBrushes.AccentBackground, "chip"));
        primaryKey.Setters.Add(new Setter(Border.BorderBrushProperty, VsThemeBrushes.AccentBorder, "chip"));
        primaryKey.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "chip"));
        primaryKey.Setters.Add(new Setter(TextBlock.ForegroundProperty, VsThemeBrushes.ListForeground, "chipText"));
        chipTemplate.Triggers.Add(primaryKey);

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var items = new FrameworkElementFactory(typeof(ItemsControl));
        items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(path));
        items.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(panel));
        items.SetValue(ItemsControl.ItemTemplateProperty, chipTemplate);
        items.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 6, 0));
        items.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        return new DataTemplate { VisualTree = items };
    }

    /// <summary>把一組旗標整理成徽章文字；沒有例外時回傳空清單。</summary>
    public static IReadOnlyList<string> BuildFlags(SqlColumnInfo column)
    {
        var flags = new List<string>(3);

        if (column.IsPrimaryKey)
        {
            flags.Add(PrimaryKeyFlag);
        }

        if (!column.IsNullable)
        {
            flags.Add("NOT NULL");
        }

        if (column.IsIdentity)
        {
            flags.Add("IDENTITY");
        }

        return flags;
    }

    private static Binding TemplatedParent(string path)
    {
        return new Binding(path) { RelativeSource = RelativeSource.TemplatedParent };
    }

    private static void AddTrigger(
        ControlTemplate template,
        DependencyProperty property,
        DependencyProperty target,
        object targetValue,
        string targetName)
    {
        var trigger = new Trigger { Property = property, Value = true };
        trigger.Setters.Add(new Setter(target, targetValue, targetName));
        template.Triggers.Add(trigger);
    }
}
