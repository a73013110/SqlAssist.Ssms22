using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 會換字的那一行：膠囊、清單抬頭與附條的摘要共用。字變了，舊字往上離開、新字從下面進來。
/// </summary>
/// <remarks>
/// 計數每一輪都可能變，原地換字時眼睛抓不到「變了」，只看到閃一下。位移只走 RenderTransform，
/// 不改版面尺寸；超出這一行的部分由 <see cref="UIElement.ClipToBounds"/> 裁掉。
/// </remarks>
internal sealed class NotificationTicker : Grid
{
    private readonly TextBlock _current;
    private readonly TextBlock _leaving;
    private readonly TranslateTransform _currentShift = new();
    private readonly TranslateTransform _leavingShift = new();

    /// <param name="create">建立一行字；兩份字要同一種字型與色彩，由呼叫端決定是抬頭還是淡色摘要。</param>
    public NotificationTicker(Func<TextBlock> create)
    {
        if (create is null) throw new ArgumentNullException(nameof(create));
        ClipToBounds = true;
        _leaving = Prepare(create(), _leavingShift);
        _leaving.Visibility = Visibility.Collapsed;
        _leaving.IsHitTestVisible = false;
        _current = Prepare(create(), _currentShift);
        Children.Add(_leaving);
        Children.Add(_current);
    }

    public string Text => _current.Text;

    internal TextBlock Current => _current;

    internal bool IsRolling => _leaving.Visibility == Visibility.Visible;

    public void SetText(string text, bool motion)
    {
        text ??= "";
        if (_current.Text == text) return;
        var previous = _current.Text;
        _current.Text = text;
        _current.ToolTip = text.Length > 0 ? text : null;
        StopMotion();
        // 第一次出現沒有舊字可以送走；那是出現，不是換字。
        if (!motion || previous.Length == 0) return;

        _leaving.Text = previous;
        _leaving.Visibility = Visibility.Visible;
        var leave = NotificationMotion.Ease(0, -NotificationMotion.RollShift, NotificationMotion.Roll);
        leave.FillBehavior = FillBehavior.Stop;
        var fade = NotificationMotion.Ease(1, 0, NotificationMotion.Roll);
        fade.FillBehavior = FillBehavior.Stop;
        fade.Completed += (_, _) => { if (_leaving.Text == previous) _leaving.Visibility = Visibility.Collapsed; };
        _leavingShift.BeginAnimation(TranslateTransform.YProperty, leave);
        _leaving.BeginAnimation(OpacityProperty, fade);

        var enter = NotificationMotion.Ease(NotificationMotion.RollShift, 0, NotificationMotion.Roll);
        enter.FillBehavior = FillBehavior.Stop;
        var appear = NotificationMotion.Ease(0, 1, NotificationMotion.Roll);
        appear.FillBehavior = FillBehavior.Stop;
        _currentShift.BeginAnimation(TranslateTransform.YProperty, enter);
        _current.BeginAnimation(OpacityProperty, appear);
    }

    public void StopMotion()
    {
        _leavingShift.BeginAnimation(TranslateTransform.YProperty, null);
        _leaving.BeginAnimation(OpacityProperty, null);
        _leaving.Visibility = Visibility.Collapsed;
        _currentShift.BeginAnimation(TranslateTransform.YProperty, null);
        _current.BeginAnimation(OpacityProperty, null);
    }

    private static TextBlock Prepare(TextBlock text, TranslateTransform shift)
    {
        text.Margin = new Thickness(0);
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.RenderTransform = shift;
        return text;
    }
}
