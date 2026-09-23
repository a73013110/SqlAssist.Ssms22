using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SqlAssist.Ssms22.UI;

/// <summary>清單列的容器樣式與它的進場／退場動畫；SQL Memory 與 SQL Search 的卡片共用這一份。</summary>
/// <remarks>
/// 卡片是清單列的<b>容器</b>，列的內容由各功能的 <c>DataTemplate</c> 決定（<c>SqlAssistChrome.SqlMemory.cs</c>
/// 與 <c>SqlAssistChrome.Search.cs</c>）。容器這一層沒有任何領域語意，所以名稱不帶 Memory：
/// 動畫長度叫 <see cref="CardEnterDuration"/> 而不是 CardEnterDuration，下一個視窗才
/// 看得出它本來就該用這一份，而不是在借 SQL Memory 的東西。
/// </remarks>
internal static partial class SqlAssistChrome
{
    /// <param name="motion">null 讀全域動畫設定；測試明確指定，不受執行環境的 Windows 動畫偏好左右。</param>
    /// <param name="removable">
    /// 列資料有 <c>IsRemoving</c> 才加退場。沒有刪除動作的清單（搜尋結果）傳 false：
    /// 留著那條繫結只會在每一列上找一個不存在的屬性，而那是靜默失敗。
    /// </param>
    /// <param name="checkable">
    /// 列資料實作 <see cref="ISqlCheckableRow"/> 才加「已勾選」的外框；理由同 <paramref name="removable"/>。
    /// </param>
    public static Style CreateSqlCardStyle(bool? motion = null, bool removable = true, bool checkable = false)
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
        if (animate) AddCardMotion(border, template, removable);
        AddTrigger(template, UIElement.IsMouseOverProperty, TextElement.ForegroundProperty, ThemeBrush.SelectedForeground, "card");
        AddTrigger(template, UIElement.IsMouseOverProperty, Control.ForegroundProperty, ThemeBrush.SelectedForeground);
        AddTrigger(template, ListBoxItem.IsSelectedProperty, Border.BackgroundProperty, ThemeBrush.RowSelected, "card");
        AddTrigger(template, ListBoxItem.IsSelectedProperty, TextElement.ForegroundProperty, ThemeBrush.SelectedForeground, "card");
        AddTrigger(template, ListBoxItem.IsSelectedProperty, Control.ForegroundProperty, ThemeBrush.SelectedForeground);
        AddTrigger(template, ListBoxItem.IsSelectedProperty, Border.BorderBrushProperty, ThemeBrush.AccentBorder, "card");
        AddTrigger(template, UIElement.IsKeyboardFocusWithinProperty, Border.BorderBrushProperty, ThemeBrush.AccentBorder, "card");
        if (checkable) AddCheckedCardTrigger(template);
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
    internal static readonly System.TimeSpan CardEnterDuration = System.TimeSpan.FromMilliseconds(180);

    /// <summary>刪除列淡出的時間；清單等它結束才真正移除，動畫關閉時立即移除。</summary>
    internal static readonly System.TimeSpan CardExitDuration = System.TimeSpan.FromMilliseconds(140);

    /// <remarks>
    /// 以列資料的 <c>IsNew</c>／<c>IsRemoving</c> 觸發，而不是容器的 Loaded：清單是 recycling 虛擬化，
    /// 捲動時重用的容器每次都會 Loaded，綁在那裡就會一路重播。位移走 RenderTransform，不推動其他列。
    /// </remarks>
    /// <param name="removable">列資料有 <c>IsRemoving</c> 才加退場；沒有刪除動作的清單不留一條找不到屬性的繫結。</param>
    internal static void AddCardMotion(FrameworkElementFactory card, ControlTemplate template, bool removable = true)
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
        enter.EnterActions.Add(new BeginStoryboard { Storyboard = Motion(0, 1, 6, 0, CardEnterDuration, FillBehavior.Stop) });
        template.Triggers.Add(enter);
        if (!removable) return;
        // 退場保持結束值直到列被移除；可中途反向：刪除失敗或容器被回收給別的列時，從當下值回到基底。
        var exit = new DataTrigger { Binding = new Binding("IsRemoving"), Value = true };
        exit.EnterActions.Add(new BeginStoryboard { Storyboard = Motion(null, 0, null, -4, CardExitDuration, FillBehavior.HoldEnd) });
        exit.ExitActions.Add(new BeginStoryboard { Storyboard = Motion(null, 1, null, 0, CardExitDuration, FillBehavior.Stop) });
        template.Triggers.Add(exit);
    }
}
