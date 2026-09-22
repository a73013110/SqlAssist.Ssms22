using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SqlAssist.Core.Settings;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 動作的語意色調；只換停駐與按下的配對色，靜止外觀仍是同一顆按鈕。
/// </summary>
/// <remarks>
/// 語意色是稀少的警示，不是分類標籤：每顆按鈕都上色，紅色就不再讀成「小心」。
/// 新增一種色調＝加一個列舉值、在 <see cref="ThemePalette"/> 推導一組角色，呼叫端不必自己配色。
/// </remarks>
internal enum SqlActionTone { Neutral, Danger, Favorite }

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
internal static partial class SqlAssistChrome
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

    /// <summary>
    /// 版面節奏；哪一階用在哪裡與「誰宣告間距」見 <c>docs/ui-guidelines.md</c>。
    /// </summary>
    /// <remarks>
    /// 節奏只寫在文件裡而每一個呼叫端各填一個數字的那一版，落地的是 4／6／8／12／16 五階，
    /// 多出來的兩階沒有人說得出它們代表哪一級，而調整其中一處也改不到並排的另一處。
    /// </remarks>
    public static class Spacing
    {
        /// <summary>同一塊裡的兩列之間，以及標籤到欄位。</summary>
        public const double Tight = 4;

        /// <summary>區塊之間，以及停靠工具窗的外距。</summary>
        public const double Group = 8;

        /// <summary>對話框的區塊與頁尾之間，以及視窗外距。</summary>
        public const double Block = 16;

        /// <summary>雙欄之間。</summary>
        public const double Columns = 18;
    }

    private static volatile SqlAssistSettings _settings = new();

    /// <summary>由設定服務每次重讀後推入；UI 層不直接認識平台的設定服務，才能單獨編進測試。</summary>
    internal static void UseSettings(SqlAssistSettings settings) => _settings = settings;

    /// <summary>自製介面現在該不該播動畫；每一個動畫表面都問這一處，不自行讀 Windows 偏好。</summary>
    public static bool MotionEnabled => MotionPolicy(_settings.Animations, _settings.IgnoreWindowsAnimationSetting,
        SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast);

    // 高對比與總開關優先；覆寫只略過 Windows 動畫偏好，不寫回 OS。
    internal static bool MotionPolicy(bool enabled, bool ignoreWindows, bool windowsAnimation, bool highContrast) =>
        enabled && !highContrast && (ignoreWindows || windowsAnimation);

    /// <summary>內容表面出現時的淡入長度；共用一個數字，改一處就是全部。</summary>
    public static readonly TimeSpan AppearDuration = TimeSpan.FromMilliseconds(120);

    private static readonly CubicEase AppearEase = FrozenEaseOut();

    /// <summary>
    /// 內容表面的出現：只做透明度，不縮放也不位移。
    /// </summary>
    /// <remarks>
    /// 浮動預覽與 SQL Memory 的復原卡片共用這一個出現動畫。內容本身沒有狀態要說，
    /// 放大或回彈只是在搶讀 SQL 的注意力；120 毫秒短到不擋操作，仍看得出它是長出來的。
    ///
    /// 結束後把屬性交還基底值（<see cref="FillBehavior.Stop"/>），關著動畫時也先清掉上一次的：
    /// 保留結束值的動畫會壓過之後的直接指定，「關掉動畫」就會變成關不掉。
    /// </remarks>
    /// <param name="motion">null 讀全域動畫設定；測試明確指定，不受執行環境的 Windows 動畫偏好左右。</param>
    public static void PlayAppear(UIElement element, bool? motion = null)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (!(motion ?? MotionEnabled))
        {
            element.Opacity = 1;
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, AppearDuration)
        {
            EasingFunction = AppearEase,
            FillBehavior = FillBehavior.Stop
        });
    }

    private static CubicEase FrozenEaseOut()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    /// <summary>視窗內的產品標誌：優先呈現產品圖示，保留小尺寸辨識度並跟隨 Fluent 配色。</summary>
    public static Border CreateBrandMark(ImageSource? imageSource = null)
    {
        UIElement content;
        if (imageSource != null)
        {
            var image = new Image
            {
                Source = imageSource,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            content = image;
        }
        else
        {
            // 在未提供圖示來源時以向量路徑作為安全後備。
            var glyph = new Canvas { Width = 48, Height = 48 };
            var database = new Path
            {
                Data = Geometry.Parse(
                    "M10,14 C10,9.3 28,9.3 28,14 L28,32 C28,36.7 10,36.7 10,32 Z " +
                    "M10,14 C10,18.7 28,18.7 28,14 M10,23 C10,27.7 28,27.7 28,23"),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            }.WithTheme(Shape.StrokeProperty, ThemeBrush.ListForeground);
            var caret = new Path
            {
                Data = Geometry.Parse("M33,19 H38 M35.5,19 V34 M33,34 H38"),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            }.WithTheme(Shape.StrokeProperty, ThemeBrush.AccentBorder);
            glyph.Children.Add(database);
            glyph.Children.Add(caret);
            content = new Viewbox { Child = glyph, Stretch = Stretch.Uniform };
        }

        return new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = content
        }.WithTheme(Border.BackgroundProperty, ThemeBrush.AccentBackground)
            .WithTheme(Border.BorderBrushProperty, ThemeBrush.Hairline);
    }

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

    /// <summary>
    /// 右下角的調整大小握把。
    /// </summary>
    /// <remarks>
    /// 給沒有原生標題列的浮動內容用：整塊 14×14 都要抓得到，所以底是透明的
    /// <see cref="Border"/> 而不是只有兩條線——只有線條可以命中的話，使用者會覺得
    /// 這個角落時靈時不靈。實際的縮放交給呼叫端，這裡只提供外觀與游標。
    /// </remarks>
    public static Thumb CreateResizeGrip()
    {
        var area = new FrameworkElementFactory(typeof(Border));
        area.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        var lines = new FrameworkElementFactory(typeof(Path));
        lines.SetValue(Path.DataProperty, Geometry.Parse("M12,4 L4,12 M12,8 L8,12"));
        lines.SetValue(Shape.StrokeThicknessProperty, 1.0);
        lines.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        lines.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        lines.SetResourceReference(Shape.StrokeProperty, ThemeBrush.DimForeground);
        area.AppendChild(lines);

        return new Thumb
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 2, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = System.Windows.Input.Cursors.SizeNWSE,
            Focusable = false,
            ToolTip = "拖曳調整大小",
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = area }
        };
    }

    /// <summary>
    /// 小型限高區塊的覆蓋式捲軸：3 DIP 握把疊在內容右緣，不佔版面寬度。
    /// </summary>
    /// <remarks>
    /// 只有縱向捲軸、沒有軌道點擊，命中範圍也小，所以只給高度有限的輔助區塊；
    /// 對話框、資料格與編輯區維持 SSMS 原生捲軸。內容右緣要自留空隙，握把才不會壓字。
    /// </remarks>
    public static void ApplyOverlayScroll(ScrollViewer scroll)
    {
        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        scroll.Template = CreateOverlayScrollTemplate();
    }

    /// <summary>
    /// 單列、放不下就橫向捲動的資訊列：預覽摘要與已選條件列共用同一份。
    /// </summary>
    /// <remarks>
    /// 不畫捲軸（<see cref="ScrollBarVisibility.Hidden"/> 允許延伸但不留軌道），也不換行：
    /// 這一列的高度必須與內容多寡無關，換行的那一版在停靠面板裡會長成三列，
    /// 而那三列換算成少看好幾筆結果。
    ///
    /// 滾輪在這一列上<b>不</b>傳給底下的內容：向下往右、向上往左，沒有溢出時也一樣攔下來——
    /// 傳下去的症狀是使用者以為自己在捲這一列，實際上捲走的是預覽裡的 SQL。
    /// 聚焦後可用 ←／→、Home／End，鍵盤才走得到捲出去的那幾顆。
    /// </remarks>
    /// <param name="automationName">整列唸出來是什麼；要說得出它可以水平捲動。</param>
    public static ScrollViewer CreateHorizontalStrip(FrameworkElement content, string automationName)
    {
        content.VerticalAlignment = VerticalAlignment.Center;

        var strip = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.HorizontalOnly,
            Focusable = true,
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "在這一列上使用滑鼠滾輪左右捲動；聚焦後可用 ← / →、Home / End。"
        };
        AutomationProperties.SetName(strip, automationName);

        strip.PreviewMouseWheel += (_, args) =>
        {
            if (args.Delta == 0) return;
            PanHorizontally(strip, args.Delta);
            args.Handled = true;
        };

        strip.PreviewKeyDown += (_, args) =>
        {
            if (args.KeyboardDevice.Modifiers != ModifierKeys.None) return;
            switch (args.Key)
            {
                case Key.Left: strip.LineLeft(); break;
                case Key.Right: strip.LineRight(); break;
                case Key.Home: strip.ScrollToLeftEnd(); break;
                case Key.End: strip.ScrollToRightEnd(); break;
                default: return;
            }

            args.Handled = true;
        };

        return strip;
    }

    /// <summary>一格滾輪換多少水平位移；資訊列與 Shift＋滾輪共用同一個手感。</summary>
    private const double WheelPanFactor = 0.4;

    /// <summary>
    /// 讓一塊有水平捲軸的內容支援 Shift＋滾輪左右捲動。
    /// </summary>
    /// <remarks>
    /// WPF 的 <see cref="ScrollViewer"/> 原生只認垂直滾輪，Shift＋滾輪什麼都不做；而有水平
    /// 捲軸的地方（不換行的 SQL 預覽、差異比對）唯一的左右捲動方式就只剩拖曳那條捲軸，
    /// 在停靠面板裡那是一條十幾 DIP 的軌道。
    ///
    /// 掛在 <see cref="UIElement.PreviewMouseWheelEvent"/> 上而不是等它冒泡：RichTextBox 這類
    /// 自己有捲動區的控制項會先把滾輪吃掉，接冒泡的那一版一次都不會被呼叫。
    ///
    /// 捲不動（沒有水平捲軸）時<b>不</b>攔下來：那一刻使用者要的是原本的垂直捲動，
    /// 攔掉等於按著 Shift 就整個捲不動。
    /// </remarks>
    public static void ApplyShiftWheelPan(FrameworkElement content)
    {
        content.PreviewMouseWheel += (_, args) =>
        {
            if (args.Delta == 0 || !ShiftHeld) return;
            if (FindScrollViewer(content) is not { } scroll || scroll.ScrollableWidth <= 0) return;

            PanHorizontally(scroll, args.Delta);
            args.Handled = true;
        };
    }

    /// <summary>
    /// 現在按著 Shift 沒有。
    /// </summary>
    /// <remarks>
    /// 產品碼其餘地方一律讀事件帶的 <see cref="KeyboardDevice"/>，而滑鼠事件上沒有那一個
    /// ——<see cref="MouseWheelEventArgs"/> 帶的是滑鼠裝置。那條規則防的是<b>合成的按鍵</b>
    /// 混進實體鍵盤狀態，而滾輪不會被合成，所以這裡問目前的鍵盤狀態是安全的。
    /// 只留這一個出處，其他地方仍然不得直接讀靜態的鍵盤。
    /// </remarks>
    private static bool ShiftHeld => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    /// <summary>向下往右、向上往左；兩處的手感由 <see cref="WheelPanFactor"/> 保持一致。</summary>
    private static void PanHorizontally(ScrollViewer scroll, double delta) =>
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - delta * WheelPanFactor);

    /// <summary>
    /// 樹裡第一個 <see cref="ScrollViewer"/>；控制項樣板套用之前回 null。
    /// </summary>
    /// <remarks>
    /// 清單續頁、差異比對的捲動與 Shift＋滾輪共用這一份：各寫一份的症狀是其中一份忘了
    /// 處理「樣板還沒套上」的那一刻，而那是視窗剛開啟的第一個版面回合。
    /// </remarks>
    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, index)) is { } child) return child;
        }

        return null;
    }

    private static ControlTemplate CreateOverlayScrollTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid));
        var presenter = new FrameworkElementFactory(typeof(ScrollContentPresenter)) { Name = "PART_ScrollContentPresenter" };
        presenter.SetBinding(ContentPresenter.ContentProperty, TemplatedParent(nameof(ContentControl.Content)));
        presenter.SetBinding(ContentPresenter.ContentTemplateProperty, TemplatedParent(nameof(ContentControl.ContentTemplate)));
        presenter.SetBinding(ScrollContentPresenter.CanContentScrollProperty, TemplatedParent(nameof(ScrollViewer.CanContentScroll)));
        root.AppendChild(presenter);

        // 本地值蓋過 VsThemeBrushes 發布的原生 ScrollBar 樣式，否則寬度與樣板會被換回去。
        var bar = new FrameworkElementFactory(typeof(ScrollBar)) { Name = "PART_VerticalScrollBar" };
        bar.SetValue(FrameworkElement.WidthProperty, 3d);
        bar.SetValue(FrameworkElement.MinWidthProperty, 0d);
        bar.SetValue(FrameworkElement.MaxWidthProperty, 3d);
        bar.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        bar.SetValue(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Arrow);
        bar.SetValue(ScrollBar.OrientationProperty, Orientation.Vertical);
        bar.SetValue(RangeBase.MinimumProperty, 0d);
        bar.SetBinding(RangeBase.MaximumProperty, TemplatedParent(nameof(ScrollViewer.ScrollableHeight)));
        bar.SetBinding(ScrollBar.ViewportSizeProperty, TemplatedParent(nameof(ScrollViewer.ViewportHeight)));
        bar.SetBinding(RangeBase.ValueProperty,
            new Binding(nameof(ScrollViewer.VerticalOffset)) { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.OneWay });
        bar.SetBinding(UIElement.VisibilityProperty, TemplatedParent(nameof(ScrollViewer.ComputedVerticalScrollBarVisibility)));
        bar.SetValue(Control.TemplateProperty, new ControlTemplate(typeof(ScrollBar))
        {
            VisualTree = new FrameworkElementFactory(typeof(OverlayScrollTrack)) { Name = "PART_Track" }
        });
        root.AppendChild(bar);
        return new ControlTemplate(typeof(ScrollViewer)) { VisualTree = root };
    }

    /// <summary><see cref="Track.Thumb"/> 不是相依性屬性，樣板工廠設不到，只能由子類別自己放。</summary>
    private sealed class OverlayScrollTrack : Track
    {
        public OverlayScrollTrack()
        {
            IsDirectionReversed = true;
            var grip = new FrameworkElementFactory(typeof(Border)) { Name = "grip" };
            grip.SetValue(Border.CornerRadiusProperty, new CornerRadius(1.5));
            grip.SetResourceReference(Border.BackgroundProperty, ThemeBrush.ScrollThumb);
            var template = new ControlTemplate(typeof(Thumb)) { VisualTree = grip };
            AddTrigger(template, UIElement.IsMouseOverProperty, Border.BackgroundProperty, ThemeBrush.DimForeground, "grip");
            AddTrigger(template, Thumb.IsDraggingProperty, Border.BackgroundProperty, ThemeBrush.ListForeground, "grip");
            Thumb = new Thumb { MinHeight = 16, Cursor = System.Windows.Input.Cursors.Hand, Template = template };
        }
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

    /// <summary>單行脈絡資訊；不再用第二個標題搶走內容的閱讀空間。</summary>
    public static TextBlock CreateMetadataText(string text, Metrics metrics)
    {
        var metadata = CreateStatusText(metrics);
        metadata.Text = text;
        return metadata;
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
    public static ControlTemplate CreateGhostButtonTemplate(SqlActionTone tone = SqlActionTone.Neutral)
    {
        return CreateButtonTemplate(primary: false, tone);
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
        return CreateButtonTemplate(primary: true, SqlActionTone.Neutral);
    }

    private static ControlTemplate CreateButtonTemplate(bool primary, SqlActionTone tone)
    {
        // 色調只換互動狀態用的三個角色；中性仍走選取色，樣板結構與觸發器完全相同。
        var (hover, pressed, paired) = tone switch
        {
            SqlActionTone.Danger => (ThemeBrush.DangerBackground, ThemeBrush.DangerPressed, ThemeBrush.DangerForeground),
            SqlActionTone.Favorite => (ThemeBrush.FavoriteBackground, ThemeBrush.FavoritePressed, ThemeBrush.FavoriteForeground),
            _ => (ThemeBrush.RowSelected, ThemeBrush.RowPressed, ThemeBrush.SelectedForeground)
        };
        var background = new FrameworkElementFactory(typeof(Border)) { Name = "bg" };
        // ContentPresenter 的附加前景預設是黑色；明確承接控制項，互動 trigger 才能覆寫同一來源。
        background.SetBinding(TextElement.ForegroundProperty, TemplatedParent(nameof(Control.Foreground)));
        if (primary)
        {
            // 主要動作本身是破壞性時，靜止底色就用語意色；停駐底色與中性主要動作一樣不另外加深。
            background.SetResourceReference(Border.BackgroundProperty, tone == SqlActionTone.Danger ? hover : ThemeBrush.AccentBackground);
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
            Border.BackgroundProperty, hover, "bg");

        AddTrigger(template, UIElement.IsMouseOverProperty,
            TextElement.ForegroundProperty, paired, "bg");
        AddTrigger(template, UIElement.IsMouseOverProperty,
            Control.ForegroundProperty, paired);
        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty,
            Border.BackgroundProperty, hover, "bg");
        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty,
            TextElement.ForegroundProperty, paired, "bg");
        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty,
            Control.ForegroundProperty, paired);
        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty,
            Border.BorderBrushProperty, ThemeBrush.Border, "bg");

        // 按下的回饋優先於焦點，否則滑鼠按下取得焦點後會把 pressed 底色蓋回去。
        AddTrigger(template, ButtonBase.IsPressedProperty,
            Border.BackgroundProperty, pressed, "bg");

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
        // 預設前景不可放 local value，否則樣板的 hover／focus 配對色無法覆寫。
        var style = new Style(typeof(Button));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.ListForeground));
        return new Button
        {
            Content = text,
            Padding = new Thickness(12, 4, 12, 5),
            FontFamily = InterfaceFont,
            FontSize = metrics.Body, Style = style,
            Template = primary ? CreatePrimaryButtonTemplate() : CreateGhostButtonTemplate()
        };
    }

    /// <summary>精簡確認內容：影響說明與單一頁尾，不重複原生標題列。</summary>
    public static Grid CreateConfirmationContent(
        string message, string detail, string action, out Button confirm, out Button cancel)
    {
        var root = new Grid { Margin = DialogPadding };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = message,
            FontFamily = InterfaceFont,
            FontSize = DefaultMetrics.Body,
            TextWrapping = TextWrapping.Wrap
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.WindowForeground));
        var hint = CreateHint(detail, DefaultMetrics);
        hint.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(hint);
        // 長片段名稱只捲動訊息本身，避免把取消按鈕推到視窗外。
        root.Children.Add(new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 240,
            Focusable = false
        });

        cancel = CreateButton("取消", DefaultMetrics);
        // Enter 與 Esc 都先保留草稿；只有明確移到動作按鈕後才允許破壞性操作。
        cancel.IsDefault = true;
        cancel.IsCancel = true;
        confirm = CreateButton(action, DefaultMetrics, primary: true);
        var footer = CreateDialogFooter(null, cancel, confirm);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        System.Windows.Input.FocusManager.SetFocusedElement(root, cancel);
        return root;
    }

    /// <summary>底部那一條回饋訊息；平常是空的，所以永遠比內容淡。</summary>
    public static TextBlock CreateStatusText(Metrics metrics)
    {
        var status = new TextBlock
        {
            FontFamily = InterfaceFont,
            FontSize = metrics.Caption,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        status.SetBinding(FrameworkElement.ToolTipProperty,
            new Binding(nameof(TextBlock.Text)) { RelativeSource = RelativeSource.Self });
        return status;
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

        // 欄位多到放不下時資料格本來就會長出水平捲軸，而 WPF 原生只認垂直滾輪：少了這一道，
        // 左右捲動只剩拖那條軌道。捲不動時它不攔滾輪，直欄的資料格不受影響。
        ApplyShiftWheelPan(grid);
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
    public static ControlTemplate CreateTextBoxTemplate() => CreateTextBoxTemplate(typeof(TextBox));

    /// <param name="targetType">
    /// 套用樣板的控制項型別。<see cref="RichTextBox"/> 與 <see cref="TextBox"/> 的外框
    /// 完全相同，但 <c>ControlTemplate</c> 的 TargetType 必須對得上，否則套不上去。
    /// </param>
    public static ControlTemplate CreateTextBoxTemplate(Type targetType)
    {
        var field = new FrameworkElementFactory(typeof(Border)) { Name = "field" };
        field.SetBinding(Border.BackgroundProperty, TemplatedParent(nameof(Control.Background)));
        field.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        field.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        field.SetValue(Border.CornerRadiusProperty, new CornerRadius(InnerRadius));
        // TextBox 的文字視圖已套用 Padding；外框再套一次會讓輸入框過高、左右縮排加倍。
        field.SetValue(Border.PaddingProperty, default(Thickness));
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

        var template = new ControlTemplate(targetType) { VisualTree = field };

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

    /// <summary>
    /// 唯讀的程式碼檢視區：與輸入欄位同一個外框，但內容可以分段上色。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="RichTextBox"/> 而不是 <see cref="TextBox"/>，是因為預覽要用顏色
    /// 分出「原本的 SQL」與「片段新增的外框」，而 <c>TextBox</c> 只有一種前景色。
    /// 仍然可以選取與複製——那是這個區塊最常見的下一步，<c>TextBlock</c> 做不到。
    /// 不換行：SQL 折行之後對不齊，寬度不夠時用水平捲軸。
    /// </remarks>
    public static RichTextBox CreateCodeViewer(Metrics metrics)
    {
        var viewer = new RichTextBox
        {
            FontFamily = CodeFont,
            FontSize = metrics.Body,
            Padding = new Thickness(8, 5, 8, 6),
            BorderThickness = new Thickness(1),
            IsReadOnly = true,
            IsDocumentEnabled = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Template = CreateTextBoxTemplate(typeof(RichTextBox))
        };
        viewer.WithTheme(RichTextBox.BackgroundProperty, ThemeBrush.ListBackground)
            .WithTheme(RichTextBox.ForegroundProperty, ThemeBrush.ListForeground)
            .WithTheme(RichTextBox.SelectionBrushProperty, ThemeBrush.RowSelected);
        return viewer;
    }

    /// <summary>
    /// 把整份文件換成一段程式碼；不換行、不留段落間距。
    /// </summary>
    /// <remarks>
    /// 頁寬要自己算：<see cref="FlowDocument"/> 預設照可視寬度折行，SQL 一折就對不齊。
    /// 但固定給一個很大的值會讓水平捲軸永遠都在，所以照最長的一行估——等寬字型的
    /// 前進寬度約是字級的 0.62 倍，寧可估寬一點點，也不要估窄而折行。
    /// </remarks>
    public static void SetCode(RichTextBox viewer, string text, IEnumerable<Inline> content)
    {
        var paragraph = new Paragraph { Margin = default };
        paragraph.Inlines.AddRange(content);
        viewer.Document = new FlowDocument(paragraph)
        {
            PagePadding = default,
            PageWidth = Math.Max(1, LongestLine(text) * viewer.FontSize * 0.62),
            FontFamily = viewer.FontFamily,
            FontSize = viewer.FontSize
        };
        viewer.ScrollToHome();
    }

    private static int LongestLine(string text)
    {
        var longest = 0;
        var current = 0;
        foreach (var character in text)
        {
            if (character == '\n' || character == '\r')
            {
                current = 0;
                continue;
            }

            current++;
            if (current > longest)
            {
                longest = current;
            }
        }

        return longest;
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

    /// <summary>
    /// 單選鈕：與核取方塊同一個尺寸與色階，只有形狀是圓的。
    /// </summary>
    /// <remarks>
    /// 形狀本身就是語意：圓的是「只能選一個」，方的是「可以選好幾個」。兩者外觀共用，
    /// 呼叫端換的只有這一份樣板；自己在功能目錄畫一顆的症狀是同一個面板裡兩種選項的
    /// 尺寸與對齊差一兩個 DIP，而那正好是看得出來卻說不出哪裡怪的差距。
    ///
    /// 內建的單選鈕與核取方塊同樣跟 Windows 佈景主題走，深色主題裡會露出白底。
    /// </remarks>
    public static ControlTemplate CreateRadioTemplate()
    {
        var layout = new FrameworkElementFactory(typeof(StackPanel));
        layout.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        layout.SetValue(Panel.BackgroundProperty, Brushes.Transparent);

        var ring = new FrameworkElementFactory(typeof(Border)) { Name = "ring" };
        ring.SetValue(FrameworkElement.WidthProperty, 14.0);
        ring.SetValue(FrameworkElement.HeightProperty, 14.0);
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        ring.SetResourceReference(Border.BackgroundProperty, ThemeBrush.SegmentTrack);
        ring.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        ring.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        ring.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        ring.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var dot = new FrameworkElementFactory(typeof(Ellipse)) { Name = "dot" };
        dot.SetValue(FrameworkElement.WidthProperty, 6.0);
        dot.SetValue(FrameworkElement.HeightProperty, 6.0);
        dot.SetResourceReference(Shape.FillProperty, ThemeBrush.ListForeground);
        dot.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        dot.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        ring.AppendChild(dot);

        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        layout.AppendChild(ring);
        layout.AppendChild(label);

        var template = new ControlTemplate(typeof(RadioButton)) { VisualTree = layout };

        AddTrigger(template, UIElement.IsMouseOverProperty, Border.BorderBrushProperty, ThemeBrush.Border, "ring");
        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty,
            Border.BorderBrushProperty, ThemeBrush.AccentBorder, "ring");

        var isChecked = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        isChecked.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "dot"));
        isChecked.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, ThemeBrush.AccentBackground, "ring"));
        isChecked.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.AccentBorder, "ring"));
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
    public static ControlTemplate CreateTabControlTemplate(bool compact = false)
    {
        var layout = new FrameworkElementFactory(typeof(DockPanel));
        layout.SetValue(DockPanel.LastChildFillProperty, true);

        var track = new FrameworkElementFactory(typeof(Border));
        track.SetValue(DockPanel.DockProperty, Dock.Top);
        track.SetResourceReference(Border.BackgroundProperty, ThemeBrush.SegmentTrack);
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        track.SetValue(Border.PaddingProperty, new Thickness(2));
        track.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        track.SetValue(FrameworkElement.MarginProperty, compact ? new Thickness(0) : new Thickness(14, 0, 14, 10));

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
        AddTrigger(template, UIElement.IsMouseOverProperty,
            Control.ForegroundProperty, ThemeBrush.ListForeground);

        var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, ThemeBrush.ListBackground, "segment"));
        selected.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.Hairline, "segment"));
        selected.Setters.Add(ThemeResourceSet.Setter(TextElement.ForegroundProperty, ThemeBrush.ListForeground, "label"));
        selected.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.ListForeground));
        template.Triggers.Add(selected);

        return template;
    }

    /// <summary>圖示加標籤的一個分頁；分頁本身沒有領域語意，哪個工具窗都用同一顆。</summary>
    public static TabItem CreateIconTab(SqlIcon icon, string label)
    {
        var style = new Style(typeof(TabItem));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.DimForeground));
        // Tooltip 是窄窗收起分頁文字之後仍讀得到名稱的地方。
        var tab = new TabItem { Header = CreateIconLabel(icon, label), Template = CreateTabItemTemplate(), Style = style, ToolTip = label };
        AutomationProperties.SetName(tab, label); return tab;
    }

    /// <summary>欄位標題：一條細線把它跟資料分開，字比資料更小也更淡。</summary>
    public static Style CreateColumnHeaderStyle(
        Metrics metrics,
        HorizontalAlignment alignment = HorizontalAlignment.Left,
        string? tooltip = null)
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
            Control.HorizontalContentAlignmentProperty, alignment));
        if (tooltip is not null)
        {
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, tooltip));
        }
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
    public static Style CreateCellTextStyle(TextAlignment alignment = TextAlignment.Left)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(10, 0, 10, 0)));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, alignment));
        // 省略只影響版面；完整值仍可從 Tooltip 讀取，不必拉寬整張表。
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,
            new Binding(nameof(TextBlock.Text)) { RelativeSource = RelativeSource.Self }));
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
        string? targetName = null)
    {
        var trigger = new Trigger { Property = property, Value = true };
        trigger.Setters.Add(ThemeResourceSet.Setter(target, targetValue, targetName));
        template.Triggers.Add(trigger);
    }
}
