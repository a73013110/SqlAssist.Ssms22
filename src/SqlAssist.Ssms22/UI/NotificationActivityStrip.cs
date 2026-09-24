using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 貼在卡片底邊的附條：左邊狀態圖示與一句摘要，右邊一個動作，整條都可以按。
/// </summary>
/// <remarks>
/// 提醒卡底下用它說「還有工作在跑，查看」，暫看活動時清單底下用它說「還有提醒，回到提醒」。
/// 取代的是提醒左側的圓形衛星：那一顆只有進度環，零進度時是一個空圓，看不出是什麼，
/// 又自帶柔影，讀起來像另一個浮起來的物件。附條用一句話說明狀態，且屬於卡片本身。
///
/// 放在一個左右內距為 <see cref="NotificationLayout.Left"/>／<see cref="NotificationLayout.Right"/> 的容器裡，
/// 由 <see cref="Place"/> 把它推到卡片邊緣；內部的圖示與文字仍落在島嶼共用的那兩條基準線上。
/// </remarks>
internal sealed class NotificationActivityStrip : Button
{
    /// <summary>含上緣髮絲線的高度。</summary>
    public const double StripHeight = 30;

    private readonly TextBlock _action;
    private Action? _collapsed;

    public NotificationActivityStrip()
    {
        SqlAssistChrome.ApplyNotificationStrip(this);
        Height = StripHeight;
        // 外框貼著島嶼 1 DIP 的邊緣內側，範本的內框再佔 1 DIP；扣掉這兩層，內容才落在共用基準線上。
        Padding = new Thickness(NotificationLayout.Left - 2, 0, NotificationLayout.TextEnd - 2, 0);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NotificationLayout.IconColumn) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Icon = new NotificationStatusIcon { Margin = NotificationLayout.StatusIconInset };
        grid.Children.Add(Icon);
        Summary = new NotificationTicker(() =>
        {
            var text = SqlAssistChrome.CreateHint("", SqlAssistChrome.DefaultMetrics);
            text.FontSize = 11;
            text.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceSet.NotificationDimKey);
            return text;
        });
        Grid.SetColumn(Summary, 1);
        grid.Children.Add(Summary);
        _action = SqlAssistChrome.CreateLabel("", SqlAssistChrome.DefaultMetrics);
        _action.FontSize = 11; _action.FontWeight = FontWeights.Normal;
        _action.Margin = new Thickness(SqlAssistChrome.Spacing.Group, 0, 0, 0);
        _action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_action, 2);
        grid.Children.Add(_action);
        Content = grid;
    }

    public NotificationStatusIcon Icon { get; }

    public NotificationTicker Summary { get; }

    public string Action => _action.Text;

    /// <summary>
    /// 在內距為 <see cref="NotificationLayout.Left"/>／<see cref="NotificationLayout.Right"/>、下距為
    /// <paramref name="bottom"/> 的容器裡，把附條推到卡片左右與底邊，上方留 <paramref name="gap"/>。
    /// </summary>
    public void Place(double gap, double bottom) =>
        Margin = new Thickness(-(NotificationLayout.Left - 1), gap, -(NotificationLayout.Right - 1), -(bottom - 1));

    /// <summary>正在淡出；淡完才收起，收起之後卡片才縮回。</summary>
    public bool IsLeaving => _collapsed is not null;

    /// <summary>顯示並換上這一輪的內容；正在離場的話就地留下來。</summary>
    /// <param name="feedback">播狀態轉換的回饋；呼叫端只在狀態真的變了、而且附條看得見時才給 true。</param>
    public void Update(string summary, NotificationVisualStatus status, string action, bool feedback, bool motion)
    {
        CancelLeave();
        Visibility = Visibility.Visible;
        Icon.SetStatus(status, feedback && motion);
        Summary.SetText(summary, motion);
        _action.Text = action;
        ToolTip = summary;
        AutomationProperties.SetName(this, summary + "，" + action);
    }

    /// <summary>
    /// 沒有活動了：先淡出，再收起並呼叫 <paramref name="collapsed"/>，由呼叫端把卡片縮回去。
    /// </summary>
    /// <remarks>
    /// 同時收起與縮卡片的話，附條那一塊會先變成一片空白的玻璃，外框才慢慢追上來。
    /// 看不見或動畫關著時直接收起，不呼叫 <paramref name="collapsed"/>：那時沒有要縮的東西。
    /// </remarks>
    public void Leave(bool motion, Action collapsed)
    {
        if (collapsed is null) throw new ArgumentNullException(nameof(collapsed));
        if (Visibility != Visibility.Visible || IsLeaving) return;
        Icon.Spin(false);
        if (!motion) { Collapse(); return; }
        _collapsed = collapsed;
        var fade = NotificationMotion.Ease(Opacity, 0, NotificationMotion.ContentFadeOut);
        fade.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_collapsed, collapsed)) return;
            Collapse();
            collapsed();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>直接收起，不播也不通知。</summary>
    public void Collapse()
    {
        _collapsed = null;
        BeginAnimation(OpacityProperty, null);
        Visibility = Visibility.Collapsed;
    }

    public void StopMotion()
    {
        Icon.StopMotion();
        Summary.StopMotion();
        if (IsLeaving) Collapse();
    }

    private void CancelLeave()
    {
        if (!IsLeaving) return;
        _collapsed = null;
        BeginAnimation(OpacityProperty, null);
    }
}
