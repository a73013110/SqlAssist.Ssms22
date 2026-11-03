using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 看得出「可以直接改」的文字欄：停駐或選到這一列時那一格浮出一圈欄位外框與一顆鉛筆，
/// 編輯時換成強調色外框，確認之後那一格淡出一抹強調底。
/// </summary>
/// <remarks>
/// 資料格的文字欄平常與唯讀欄長得一模一樣，使用者不會想到要在上面按 F2。只在停駐與選取時才提示：
/// 一整欄常駐的外框是成排框線，正是 <c>ui-guidelines</c> 不要的那一種。提示的節奏照列操作的揭露
/// （<see cref="SqlAssistChrome.RowActionRevealDuration"/>：淡入＋6 DIP 滑入），確認後的那一抹與變更記號的
/// 彈出是狀態回饋，不超過 400 ms，走 <see cref="UIElement.RenderTransform"/> 與透明度，不改版面尺寸。
///
/// 「值換了」只認同一列上的變化：資料格捲動時元件會換一列重用（recycling），換列那一次不是編輯，
/// 不能播；別的列更新而重新通知同一個值時也不播。三種動畫都受全域動畫設定控制，關掉時提示照樣出現、
/// 只是不淡入。
/// </remarks>
internal sealed class SqlEditableTextColumn : DataGridTextColumn
{
    /// <summary>確認之後那一抹強調底淡出的長度；狀態回饋這一級的上限是 400 ms。</summary>
    public static readonly TimeSpan CommitWashDuration = TimeSpan.FromMilliseconds(400);

    /// <summary>進入編輯時欄位從這個比例放大到 1；比內容表面的 0.96 更收斂，只是一格。</summary>
    internal const double EditScaleFrom = 0.97;

    /// <summary>進入編輯的那一下；與列操作的揭露同一級。</summary>
    internal static readonly TimeSpan EditEnterDuration = TimeSpan.FromMilliseconds(160);

    private const double GlyphSize = 20;

    /// <summary>剛在這一欄確認、值也真的換了的那一列；它的顯示元件重建時播那一抹。</summary>
    private object? _committedItem;

    /// <summary>開始編輯時那一格的文字；確認時拿來比，沒改就不播。</summary>
    private string? _editStart;

    /// <param name="path">繫結的屬性；雙向，按 Enter 或離開那一格時寫回。</param>
    public SqlEditableTextColumn(string path)
    {
        Binding = new Binding(path) { Mode = BindingMode.TwoWay, NotifyOnTargetUpdated = true };
        ElementStyle = SqlAssistChrome.CreateCellTextStyle();
        EditingElementStyle = SqlAssistChrome.CreateCellEditorStyle();
    }

    /// <summary>值空著時淡色斜體列出的那一句（例如可照抄的建議）；不是值，複製這一格不帶走它。</summary>
    public string? HintPath { get; set; }

    /// <summary>值與提示都空著、而停駐或選到這一列時出現的那一句，告訴使用者這裡可以寫。</summary>
    public string Placeholder { get; set; } = "";

    /// <summary>鉛筆那一顆的說明與朗讀名稱。</summary>
    public string EditLabel { get; set; } = "";

    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var text = (TextBlock)base.GenerateElement(cell, dataItem);
        var path = ((Binding)Binding).Path.Path;
        var motion = SqlAssistChrome.MotionEnabled;

        var wash = new Border { Opacity = 0, IsHitTestVisible = false, CornerRadius = new CornerRadius(SqlAssistChrome.InnerRadius - 1) }
            .WithTheme(Border.BackgroundProperty, ThemeBrush.AccentBackground);

        var layers = new Grid();
        layers.Children.Add(wash);
        layers.Children.Add(text);
        if (HintPath is not null) layers.Children.Add(CreateHint(HintPath));
        if (Placeholder.Length > 0) layers.Children.Add(CreatePlaceholder(path));

        var edit = CreateEditGlyph(motion);
        edit.Click += (_, _) => SqlAssistPlatformGuard.Run("從鉛筆開始編輯儲存格", () => BeginEdit(cell));
        layers.Children.Add(edit);

        var field = new Border
        {
            Child = layers,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(SqlAssistChrome.InnerRadius),
            Margin = new Thickness(3, 2, 3, 2),
            Cursor = Cursors.IBeam,
            Style = CreateFieldStyle(),
            SnapsToDevicePixels = true,
        };

        // 兩條路：在這一格裡打字確認時，資料格會整個換掉顯示元件，所以由欄位記著是哪一列、新的元件
        // 一出來就播；貼上、照抄與 Delete 不經過編輯，同一個元件上的值換了就播。
        if (_committedItem is not null && ReferenceEquals(_committedItem, dataItem))
        {
            _committedItem = null;
            field.Loaded += PlayOnce;
        }

        SqlCellTransitions.Watch(text, () => PlayCommitWash(wash));
        return field;

        void PlayOnce(object sender, RoutedEventArgs args)
        {
            field.Loaded -= PlayOnce;
            PlayCommitWash(wash);
        }
    }

    protected override object PrepareCellForEdit(FrameworkElement editingElement, RoutedEventArgs editingEventArgs)
    {
        var unedited = base.PrepareCellForEdit(editingElement, editingEventArgs);
        _editStart = (editingElement as TextBox)?.Text;
        return unedited;
    }

    protected override bool CommitCellEdit(FrameworkElement editingElement)
    {
        var committed = base.CommitCellEdit(editingElement);
        if (committed && editingElement is TextBox box && !string.Equals(box.Text, _editStart, StringComparison.Ordinal))
            _committedItem = box.DataContext;
        return committed;
    }

    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem)
    {
        var box = (TextBox)base.GenerateEditingElement(cell, dataItem);
        box.Margin = new Thickness(2, 1, 2, 1);
        box.Loaded += (_, _) => PlayEditEnter(box);
        return box;
    }

    /// <summary>
    /// 欄位外框：平常透明，停駐這一列時一圈髮絲線，選到時一圈較深的框——都只換筆刷，不改厚度。
    /// </summary>
    private static Style CreateFieldStyle()
    {
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent));

        var hover = RowTrigger(nameof(UIElement.IsMouseOver));
        hover.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.Hairline));
        style.Triggers.Add(hover);

        // 選取寫在停駐之後：兩個條件同時成立時，後宣告的那一個才是列自己畫的那一種。
        var selected = RowTrigger(nameof(DataGridRow.IsSelected));
        selected.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.Border));
        selected.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, ThemeBrush.ListBackground));
        style.Triggers.Add(selected);
        return style;
    }

    private static DataTrigger RowTrigger(string property) => new()
    {
        Binding = new Binding(property) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridRow), 1) },
        Value = true
    };

    private static TextBlock CreateHint(string path)
    {
        var hint = new TextBlock { Style = SqlAssistChrome.CreateCellTextStyle(), FontStyle = FontStyles.Italic, IsHitTestVisible = false }
            .WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        hint.SetBinding(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneWay });
        return hint;
    }

    /// <summary>空著的那一格在停駐或選到時說「這裡可以寫」；有值或有建議時讓開。</summary>
    private TextBlock CreatePlaceholder(string path)
    {
        var placeholder = new TextBlock { Text = Placeholder, IsHitTestVisible = false, Style = CreatePlaceholderStyle(path) };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        return placeholder;
    }

    private Style CreatePlaceholderStyle(string path)
    {
        var style = new Style(typeof(TextBlock), SqlAssistChrome.CreateCellTextStyle());
        style.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
        style.Setters.Add(new Setter(UIElement.OpacityProperty, 0.75));
        foreach (var row in new[] { nameof(UIElement.IsMouseOver), nameof(DataGridRow.IsSelected) })
        {
            var show = new MultiDataTrigger();
            show.Conditions.Add(new System.Windows.Condition(RowTrigger(row).Binding, true));
            show.Conditions.Add(new System.Windows.Condition(new Binding(path), ""));
            if (HintPath is not null) show.Conditions.Add(new System.Windows.Condition(new Binding(HintPath), null));
            show.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible));
            style.Triggers.Add(show);
        }

        return style;
    }

    /// <summary>
    /// 右緣的鉛筆：停駐或選到這一列才浮出來，按下去就編輯這一格。
    /// </summary>
    /// <remarks>
    /// 平常不佔版面也不能點（<see cref="UIElement.IsHitTestVisible"/> 跟著顯示走）：看不見的按鈕擋在文字上，
    /// 點到右緣那一截的人會莫名其妙開始編輯。
    /// </remarks>
    private Button CreateEditGlyph(bool motion)
    {
        var button = SqlAssistChrome.CreateIconButton(SqlIcon.Edit, EditLabel);
        button.MinWidth = GlyphSize; button.MinHeight = GlyphSize; button.Width = GlyphSize; button.Height = GlyphSize;
        button.Padding = new Thickness(2);
        button.Margin = new Thickness(0, 0, 2, 0);
        button.Focusable = false;
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Cursor = Cursors.Hand;
        button.RenderTransform = new TranslateTransform();

        var style = new Style(typeof(Button), button.Style);
        style.Setters.Add(new Setter(UIElement.OpacityProperty, 0d));
        style.Setters.Add(new Setter(UIElement.IsHitTestVisibleProperty, false));
        foreach (var row in new[] { nameof(UIElement.IsMouseOver), nameof(DataGridRow.IsSelected) })
        {
            var reveal = RowTrigger(row);
            reveal.Setters.Add(new Setter(UIElement.OpacityProperty, 1d));
            reveal.Setters.Add(new Setter(UIElement.IsHitTestVisibleProperty, true));
            if (motion) reveal.EnterActions.Add(new BeginStoryboard { Storyboard = RevealStoryboard() });
            style.Triggers.Add(reveal);
        }

        button.Style = style;
        return button;
    }

    /// <summary>與列操作的揭露同一份節奏：淡入、從右緣滑進 6 DIP；結束交還基底值。</summary>
    private static Storyboard RevealStoryboard()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, SqlAssistChrome.RowActionRevealDuration) { EasingFunction = ease };
        Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
        var slide = new DoubleAnimation(6, 0, SqlAssistChrome.RowActionRevealDuration) { EasingFunction = ease };
        Storyboard.SetTargetProperty(slide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));
        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        storyboard.Children.Add(fade);
        storyboard.Children.Add(slide);
        return storyboard;
    }

    /// <summary>把這一格設成目前的儲存格並開始編輯；列沒選到時一併選它。</summary>
    private void BeginEdit(DataGridCell cell)
    {
        var grid = DataGridOwner;
        if (grid is null || cell.DataContext is not { } item) return;

        if (!grid.SelectedItems.Contains(item)) grid.SelectedItem = item;
        grid.CurrentCell = new DataGridCellInfo(item, this);
        cell.Focus();
        grid.BeginEdit();
    }

    /// <summary>值在同一列上換了：那一格淡出一抹強調底。</summary>
    private static void PlayCommitWash(UIElement wash)
    {
        wash.BeginAnimation(UIElement.OpacityProperty, null);
        wash.Opacity = 0;
        if (!SqlAssistChrome.MotionEnabled) return;

        wash.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, CommitWashDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    /// <summary>
    /// 進入編輯：欄位從 <see cref="EditScaleFrom"/> 放大、從半透明浮上來，強調色外框隨焦點出現。
    /// </summary>
    private static void PlayEditEnter(FrameworkElement box)
    {
        if (!SqlAssistChrome.MotionEnabled) return;

        var scale = new ScaleTransform(EditScaleFrom, EditScaleFrom);
        box.RenderTransformOrigin = new Point(0.5, 0.5);
        box.RenderTransform = scale;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(EditScaleFrom, 1, EditEnterDuration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        box.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.6, 1, EditEnterDuration)
        {
            EasingFunction = ease, FillBehavior = FillBehavior.Stop
        });
    }
}

/// <summary>
/// 變更記號那一欄：記號出現的那一下從小彈到原尺寸，說得出「這一格剛動過」。
/// </summary>
internal sealed class SqlChangeMarkColumn : DataGridTextColumn
{
    /// <summary>記號彈出的長度；與搜尋狀態、用量徽章同一級。</summary>
    public static readonly TimeSpan PopDuration = SqlAssistChrome.SearchStatusPop;

    public SqlChangeMarkColumn(string path, string toolTip)
    {
        var style = new Style(typeof(TextBlock), SqlAssistChrome.CreateCellTextStyle(TextAlignment.Center));
        style.Setters.Add(ThemeResourceSet.Setter(TextBlock.ForegroundProperty, ThemeBrush.LinkForeground));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, toolTip));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        Binding = new Binding(path) { Mode = BindingMode.OneWay, NotifyOnTargetUpdated = true };
        IsReadOnly = true;
        ElementStyle = style;
        Width = new DataGridLength(20);
        CanUserResize = false;
    }

    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var text = (TextBlock)base.GenerateElement(cell, dataItem);
        SqlCellTransitions.Watch(text, () =>
        {
            if (text.Text.Length > 0) PlayPop(text);
        });
        return text;
    }

    /// <summary>從 0.4 彈過頭到 1.15 再收回 1；只在記號剛出現時播。</summary>
    private static void PlayPop(TextBlock mark)
    {
        if (mark.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            mark.RenderTransform = scale;
            mark.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!SqlAssistChrome.MotionEnabled) return;

        var pop = new DoubleAnimationUsingKeyFrames { Duration = PopDuration, FillBehavior = FillBehavior.Stop };
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(0.4, KeyTime.FromPercent(0)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.15, KeyTime.FromPercent(0.6), new CubicEase { EasingMode = EasingMode.EaseOut }));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }
}

/// <summary>
/// 資料格裡一格的值「在同一列上換了」：換列重用、第一次繫結與重新通知同一個值都不算。
/// </summary>
/// <remarks>
/// 不訂閱列物件的 <see cref="System.ComponentModel.INotifyPropertyChanged"/>：列物件跟著模型活，
/// 元件被回收重用好幾輪，訂閱一次就是一條漏掉的參考。改聽繫結自己的 <see cref="Binding.TargetUpdatedEvent"/>，
/// 元件與它一起走。
/// </remarks>
internal static class SqlCellTransitions
{
    public static void Watch(TextBlock text, Action changed)
    {
        // 只在這個處理常式裡記：換列重用時繫結會帶著新的那一列先更新一次，那一次的列與上一次不同，不算。
        object? item = null;
        string? value = null;

        text.Loaded += (_, _) =>
        {
            item = text.DataContext;
            value = text.Text;
        };
        text.TargetUpdated += (_, e) => SqlAssistPlatformGuard.Run("儲存格值的轉場", () =>
        {
            if (e.Property != TextBlock.TextProperty) return;
            var same = item is not null && ReferenceEquals(item, text.DataContext);
            var different = !string.Equals(value, text.Text, StringComparison.Ordinal);
            item = text.DataContext;
            value = text.Text;
            if (same && different) changed();
        });
    }
}
