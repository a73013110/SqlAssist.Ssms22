using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using SqlAssist.Core.Settings;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 整個擴充共用的外觀。
/// </summary>
/// <remarks>
/// 這裡是本擴充所有自建介面的唯一外觀來源：浮動結構預覽與程式碼片段對話框
/// 都從這裡取字型、字級、控制項樣板與間距。分成兩份的話，改一邊忘了另一邊
/// 的症狀是「兩個視窗長得像但又不完全一樣」——那比一開始就不統一更難看。
///
/// 樣板只持有語意資源鍵，不保存建立當下的 Brush。動態資源及備援由同一份
/// ThemeResourceSet 提供，主題切換不用重建控制項、樣板或資料列。
///
/// 版面的原則是「用留白分層，不用線條」：層次靠間距與極淡的底色，
/// 只有需要框住一整塊內容時才畫一條細線。
/// </remarks>
internal static class SqlAssistChrome
{
    /// <summary>介面字型；沒有 Variable 字族的機器會退回 Segoe UI。</summary>
    public static readonly FontFamily InterfaceFont = new("Segoe UI Variable Text, Segoe UI");

    /// <summary>程式碼字型；等寬才對得起 SQL 的縮排。</summary>
    public static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Courier New");

    /// <summary>與 DWM 圓角搭配的內圓角，比外框小一級才不會看起來腫。</summary>
    public const double InnerRadius = 5;

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
    /// 不跟著設定走的介面所用的一組字級。
    /// </summary>
    /// <remarks>
    /// 只有浮動預覽的字級是設定項——它貼在程式碼旁邊，要跟編輯器的字級一起讀。
    /// 對話框是獨立的視窗，沒有這個問題，因此固定在同一個基準值上：
    /// 比例一致，預設狀態下兩邊看起來就是同一套介面。
    /// </remarks>
    public static Metrics DefaultMetrics { get; } = new(SqlAssistLimits.DefaultPreviewFontSize);

    /// <summary>一塊內容的底：底色比視窗淺一階，四周一條細線。</summary>
    public static Border CreateSurface(UIElement? child = null)
    {
        return new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(InnerRadius + 1),
            SnapsToDevicePixels = true,
            Child = child
        }.WithTheme(Border.BackgroundProperty, ThemeBrush.ListBackground)
            .WithTheme(Border.BorderBrushProperty, ThemeBrush.Hairline);
    }

    /// <summary>區塊標題：靠字重而不是字級把段落分開。</summary>
    public static TextBlock CreateLabel(string text, Metrics metrics)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = InterfaceFont,
            FontSize = metrics.Caption,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 4)
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
    }

    /// <summary>欄位底下的說明；永遠比它說明的東西淡。</summary>
    public static TextBlock CreateHint(string text, Metrics metrics)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = InterfaceFont,
            FontSize = metrics.Caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 5, 0, 0)
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
    }

    /// <summary>短狀態用的圓角徽章；不能只靠顏色傳達狀態，文字仍是必要內容。</summary>
    public static Border CreateBadge(string text, Metrics metrics, bool accent = false)
    {
        return new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(InnerRadius),
            Padding = new Thickness(8, 2, 8, 3),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = InterfaceFont,
                FontSize = metrics.Caption
            }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground)
        }.WithTheme(Border.BackgroundProperty, accent ? ThemeBrush.AccentBackground : ThemeBrush.BadgeBackground)
            .WithTheme(Border.BorderBrushProperty, accent ? ThemeBrush.AccentBorder : ThemeBrush.Hairline);
    }

    /// <summary>
    /// 沒有邊框的按鈕，滑鼠移上去才長出底色。
    /// </summary>
    /// <remarks>
    /// 帶邊框的方鈕每一個都是四條線，一排三個就是十二條。
    /// 平常只留文字，需要按的時候才提示可按——與「用留白分層」是同一條原則。
    /// </remarks>
    public static ControlTemplate CreateGhostButtonTemplate()
    {
        return CreateButtonTemplate(primary: false);
    }

    /// <summary>
    /// 主要動作用的按鈕。
    /// </summary>
    /// <remarks>
    /// 使用經過對比檢查的淡主題強調色，不讓飽和底色與一般前景互相衝突。
    /// 只把幽靈按鈕的靜止狀態從透明換成淡底，不另立一套控制項。
    /// </remarks>
    public static ControlTemplate CreatePrimaryButtonTemplate()
    {
        return CreateButtonTemplate(primary: true);
    }

    private static ControlTemplate CreateButtonTemplate(bool primary)
    {
        var background = new FrameworkElementFactory(typeof(Border)) { Name = "bg" };
        if (primary)
        {
            background.SetResourceReference(Border.BackgroundProperty, ThemeBrush.AccentBackground);
        }
        else
        {
            background.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        }

        background.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        background.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        background.SetValue(Border.CornerRadiusProperty, new CornerRadius(InnerRadius));
        background.SetBinding(Border.PaddingProperty, TemplatedParent(nameof(Control.Padding)));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        background.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = background };

        AddTrigger(
            template, UIElement.IsMouseOverProperty,
            Border.BackgroundProperty, ThemeBrush.RowSelected, "bg");

        AddTrigger(template, UIElement.IsMouseOverProperty,
            TextElement.ForegroundProperty, ThemeBrush.SelectedForeground, "bg");
        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty,
            Border.BackgroundProperty, ThemeBrush.RowSelected, "bg");
        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty,
            TextElement.ForegroundProperty, ThemeBrush.SelectedForeground, "bg");
        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty,
            Border.BorderBrushProperty, ThemeBrush.Border, "bg");

        // 按下的回饋優先於焦點，否則滑鼠按下取得焦點後會把 pressed 底色蓋回去。
        AddTrigger(template, ButtonBase.IsPressedProperty,
            Border.BackgroundProperty, ThemeBrush.RowPressed, "bg");

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4, "bg"));
        template.Triggers.Add(disabled);

        return template;
    }

    /// <summary>
    /// 一顆按鈕，含字型、間距與樣板。
    /// </summary>
    /// <remarks>
    /// 兩個視窗各自組一遍的下場是同一種按鈕在兩邊高矮不一。
    /// <paramref name="primary"/> 只換靜止狀態的底色，那是同一套語言裡的一階。
    /// </remarks>
    public static Button CreateButton(string text, Metrics metrics, bool primary = false)
    {
        return new Button
        {
            Content = text,
            Padding = new Thickness(12, 4, 12, 5),
            FontFamily = InterfaceFont,
            FontSize = metrics.Body,
            Template = primary ? CreatePrimaryButtonTemplate() : CreateGhostButtonTemplate()
        }.WithTheme(Button.ForegroundProperty, ThemeBrush.ListForeground);
    }

    /// <summary>底部那一條回饋訊息；平常是空的，所以永遠比內容淡。</summary>
    public static TextBlock CreateStatusText(Metrics metrics)
    {
        return new TextBlock
        {
            FontFamily = InterfaceFont,
            FontSize = metrics.Caption,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
    }

    /// <summary>
    /// 資料格的共同底：不畫格線、交替底色分列、欄位標題只有下緣一條細線。
    /// </summary>
    /// <remarks>
    /// 交替底色只能走資料格自己的這兩個屬性。<see cref="DataGridRow.Background"/> 是
    /// 「轉移屬性」，資料格會把自己的值蓋到每一列上，優先權高過任何樣式與觸發程序——
    /// 試著用觸發程序畫交替列，結果是每一列都沒有底色。
    ///
    /// 唯讀與可編輯、選取單位、捲軸與內容選單留給呼叫端：那些是各自的行為，
    /// 不是外觀。字級可以事後覆寫，浮動預覽的字級是設定項。
    /// </remarks>
    public static DataGrid CreateDataGrid(Metrics metrics, bool transparent = false)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserResizeRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,

            // 格線是最吵的一種分隔方式：一百多列就是一百多條線。
            // 層次改交給交替底色，那是不用畫線也看得出來的。
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            AlternationCount = 2,
            BorderThickness = default,
            FontFamily = InterfaceFont,
            FontSize = metrics.Body,
            RowHeight = metrics.RowHeight,
            ColumnHeaderStyle = CreateColumnHeaderStyle(metrics),
            CellStyle = CreateCellStyle()
        }.WithTheme(DataGrid.ForegroundProperty, ThemeBrush.ListForeground)
            .WithTheme(DataGrid.AlternatingRowBackgroundProperty, ThemeBrush.RowAlternate);

        if (transparent)
        {
            grid.Background = Brushes.Transparent;
            grid.RowBackground = Brushes.Transparent;
        }
        else
        {
            grid.WithTheme(Control.BackgroundProperty, ThemeBrush.ListBackground)
                    .WithTheme(DataGrid.RowBackgroundProperty, ThemeBrush.ListBackground);
        }

        return grid;
    }

    /// <summary>
    /// 輸入欄位：圓角、一條細線，聚焦時線條換成強調色。
    /// </summary>
    /// <remarks>
    /// 捲軸的顯示方式必須自己綁回控制項的屬性。內建樣板是靠附加屬性把值傳給
    /// <c>PART_ContentHost</c> 的，換掉樣板之後那條路就斷了——程式碼欄位明明設了
    /// <see cref="ScrollBarVisibility.Auto"/> 卻捲不動，就是漏掉這兩條繫結。
    /// </remarks>
    public static ControlTemplate CreateTextBoxTemplate()
    {
        var field = new FrameworkElementFactory(typeof(Border)) { Name = "field" };
        field.SetBinding(Border.BackgroundProperty, TemplatedParent(nameof(Control.Background)));
        field.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        field.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        field.SetValue(Border.CornerRadiusProperty, new CornerRadius(InnerRadius));
        field.SetBinding(Border.PaddingProperty, TemplatedParent(nameof(Control.Padding)));
        field.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(UIElement.FocusableProperty, false);
        host.SetValue(Control.PaddingProperty, default(Thickness));
        host.SetBinding(
            ScrollViewer.HorizontalScrollBarVisibilityProperty,
            TemplatedParent(nameof(TextBoxBase.HorizontalScrollBarVisibility)));
        host.SetBinding(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            TemplatedParent(nameof(TextBoxBase.VerticalScrollBarVisibility)));
        field.AppendChild(host);

        var template = new ControlTemplate(typeof(TextBox)) { VisualTree = field };

        AddTrigger(
            template, UIElement.IsMouseOverProperty,
            Border.BorderBrushProperty, ThemeBrush.Border, "field");

        AddTrigger(
            template, UIElement.IsKeyboardFocusWithinProperty,
            Border.BorderBrushProperty, ThemeBrush.AccentBorder, "field");

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5, "field"));
        template.Triggers.Add(disabled);

        return template;
    }

    /// <summary>套用輸入欄位的一整組設定；呼叫端只要負責內容與版面。</summary>
    public static TextBox CreateTextBox(Metrics metrics)
    {
        return new TextBox
        {
            FontFamily = InterfaceFont,
            FontSize = metrics.Body,
            Padding = new Thickness(8, 5, 8, 6),
            BorderThickness = new Thickness(1),
            Template = CreateTextBoxTemplate()
        }.WithTheme(TextBox.BackgroundProperty, ThemeBrush.ListBackground)
            .WithTheme(TextBox.ForegroundProperty, ThemeBrush.ListForeground)
            .WithTheme(TextBox.CaretBrushProperty, ThemeBrush.ListForeground)
            .WithTheme(TextBox.SelectionBrushProperty, ThemeBrush.RowSelected);
    }

    /// <summary>下拉選單的字型、色彩與基本尺寸。</summary>
    public static ComboBox CreateComboBox(Metrics metrics)
    {
        return new ComboBox
        {
            MinHeight = metrics.RowHeight,
            Padding = new Thickness(8, 3, 8, 3),
            FontFamily = InterfaceFont,
            FontSize = metrics.Body,
            BorderThickness = new Thickness(1)
        }.WithTheme(ComboBox.ForegroundProperty, ThemeBrush.ListForeground)
            .WithTheme(ComboBox.BackgroundProperty, ThemeBrush.ListBackground)
            .WithTheme(ComboBox.BorderBrushProperty, ThemeBrush.Hairline);
    }

    /// <summary>
    /// 核取方塊：自己畫一個圓角小方塊。
    /// </summary>
    /// <remarks>
    /// 內建的核取方塊跟的是 Windows 佈景主題而不是 SSMS 的，
    /// 深色主題裡會出現一個白底的方框浮在暗色面板上。
    /// </remarks>
    public static ControlTemplate CreateCheckBoxTemplate()
    {
        var layout = new FrameworkElementFactory(typeof(StackPanel));
        layout.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        layout.SetValue(Panel.BackgroundProperty, Brushes.Transparent);

        var box = new FrameworkElementFactory(typeof(Border)) { Name = "box" };
        box.SetValue(FrameworkElement.WidthProperty, 14.0);
        box.SetValue(FrameworkElement.HeightProperty, 14.0);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        box.SetResourceReference(Border.BackgroundProperty, ThemeBrush.SegmentTrack);
        box.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        box.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        box.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var check = new FrameworkElementFactory(typeof(Path)) { Name = "check" };
        check.SetValue(Path.DataProperty, Geometry.Parse("M 2,6.5 L 4.8,9.3 L 10,3.2"));
        check.SetResourceReference(Shape.StrokeProperty, ThemeBrush.ListForeground);
        check.SetValue(Shape.StrokeThicknessProperty, 1.6);
        check.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        check.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        check.SetValue(Shape.StrokeLineJoinProperty, PenLineJoin.Round);
        check.SetValue(Shape.StretchProperty, Stretch.None);
        check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
        check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        box.AppendChild(check);

        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        layout.AppendChild(box);
        layout.AppendChild(label);

        var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = layout };

        AddTrigger(
            template, UIElement.IsMouseOverProperty,
            Border.BorderBrushProperty, ThemeBrush.Border, "box");

        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty,
            Border.BorderBrushProperty, ThemeBrush.AccentBorder, "box");

        var isChecked = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        isChecked.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "check"));
        isChecked.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, ThemeBrush.AccentBackground, "box"));
        isChecked.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.AccentBorder, "box"));
        template.Triggers.Add(isChecked);

        return template;
    }

    /// <summary>清單的一列：與分段控制器同一種圓角，選取靠底色而不是外框。</summary>
    public static Style CreateListItemStyle(Metrics metrics)
    {
        var row = new FrameworkElementFactory(typeof(Border)) { Name = "row" };
        row.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        row.SetValue(Border.CornerRadiusProperty, new CornerRadius(InnerRadius));
        row.SetValue(Border.PaddingProperty, new Thickness(10, 4, 10, 5));
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1));
        row.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = row };

        AddTrigger(
            template, UIElement.IsMouseOverProperty,
            Border.BackgroundProperty, ThemeBrush.RowHover, "row");
        AddTrigger(template, UIElement.IsMouseOverProperty,
            TextElement.ForegroundProperty, ThemeBrush.SelectedForeground, "row");

        // 選取寫在滑鼠之後：兩個條件同時成立時，後宣告的那一個才是使用者要看的。
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, ThemeBrush.RowSelected, "row"));
        selected.Setters.Add(ThemeResourceSet.Setter(TextElement.ForegroundProperty, ThemeBrush.SelectedForeground, "row"));
        template.Triggers.Add(selected);

        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, InterfaceFont));
        style.Setters.Add(new Setter(Control.FontSizeProperty, metrics.Body));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.ListForeground));
        style.Setters.Add(new Setter(
            Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
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
        track.SetResourceReference(Border.BackgroundProperty, ThemeBrush.SegmentTrack);
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
        segment.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        segment.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        segment.SetValue(Border.CornerRadiusProperty, new CornerRadius(InnerRadius));
        segment.SetValue(Border.PaddingProperty, new Thickness(12, 3, 12, 4));

        var label = new FrameworkElementFactory(typeof(ContentPresenter)) { Name = "label" };
        label.SetBinding(ContentPresenter.ContentProperty, TemplatedParent(nameof(TabItem.Header)));
        label.SetResourceReference(TextElement.ForegroundProperty, ThemeBrush.DimForeground);
        label.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        segment.AppendChild(label);

        var template = new ControlTemplate(typeof(TabItem)) { VisualTree = segment };

        // 滑鼠掃過只把字提亮，不加底色——底色是「被選中」的專屬訊號。
        AddTrigger(
            template, UIElement.IsMouseOverProperty,
            TextElement.ForegroundProperty, ThemeBrush.ListForeground, "label");

        var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, ThemeBrush.ListBackground, "segment"));
        selected.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.Hairline, "segment"));
        selected.Setters.Add(ThemeResourceSet.Setter(TextElement.ForegroundProperty, ThemeBrush.ListForeground, "label"));
        template.Triggers.Add(selected);

        return template;
    }

    /// <summary>欄位標題：一條細線把它跟資料分開，字比資料更小也更淡。</summary>
    public static Style CreateColumnHeaderStyle(Metrics metrics)
    {
        var style = new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(ThemeResourceSet.Setter(Control.BorderBrushProperty, ThemeBrush.Hairline));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.DimForeground));
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
        selected.Setters.Add(ThemeResourceSet.Setter(Control.BackgroundProperty, ThemeBrush.RowSelected));
        selected.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.SelectedForeground));
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
    /// 編輯中的儲存格。
    /// </summary>
    /// <remarks>
    /// 沒有這一份，按下去編輯的那一格會換成內建樣式的輸入欄——白底黑字，
    /// 在深色主題裡就是一格突然亮起來的白。
    /// </remarks>
    public static Style CreateCellEditorStyle()
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(ThemeResourceSet.Setter(Control.BackgroundProperty, ThemeBrush.ListBackground));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.ListForeground));
        style.Setters.Add(ThemeResourceSet.Setter(TextBoxBase.CaretBrushProperty, ThemeBrush.ListForeground));
        style.Setters.Add(ThemeResourceSet.Setter(TextBoxBase.SelectionBrushProperty, ThemeBrush.RowSelected));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, default(Thickness)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
        style.Setters.Add(new Setter(
            Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        return style;
    }

    private static Binding TemplatedParent(string path)
    {
        return new Binding(path) { RelativeSource = RelativeSource.TemplatedParent };
    }

    private static void AddTrigger(
        ControlTemplate template,
        DependencyProperty property,
        DependencyProperty target,
        ThemeBrush targetValue,
        string targetName)
    {
        var trigger = new Trigger { Property = property, Value = true };
        trigger.Setters.Add(ThemeResourceSet.Setter(target, targetValue, targetName));
        template.Triggers.Add(trigger);
    }
}
