using System;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 通知表面的時長、緩動與狀態回饋；舊卡片與通知島共用這一份。
/// </summary>
/// <remarks>
/// 散在卡片、列與 Chrome 三處各寫一次的版本，改一個數字要找三個地方，而漏掉的那一處
/// 只有在兩個表面並排時才看得出節奏不一樣。尺寸變形（通知島的寬、高與圓角）走
/// <see cref="SpringMotion"/>，不在這裡：那是可中斷的揭露動畫，不是固定長度的補間。
/// </remarks>
internal static class NotificationMotion
{
    /// <summary>表面出現：滑入與淡入。</summary>
    public const int Enter = 300;

    /// <summary>表面收場：淡出並縮小；到期判斷（<c>NotificationLifecycle</c>）等的也是這一段。</summary>
    public const int Exit = 220;

    /// <summary>展開箭頭轉半圈。</summary>
    public const int Chevron = 200;

    /// <summary>明細展開／收合與執行中的新列長出高度。</summary>
    public const int Reveal = 260;

    /// <summary>進度條從目前值接續到新的比例。</summary>
    public const int Progress = 320;

    /// <summary>明細展開時整塊的淡入淡出。</summary>
    public const int DetailFade = 220;

    /// <summary>新列淡入。</summary>
    public const int RowFade = 240;

    /// <summary>抬頭或膠囊上唯一那一個進度圈轉一圈。</summary>
    public const int Spin = 1100;

    /// <summary>通知島換內容：舊內容淡出。</summary>
    public const int ContentFadeOut = 120;

    /// <summary>通知島換內容：新內容晚這麼久才開始，兩份不會同時搶視線。</summary>
    public const int ContentDelay = 60;

    /// <summary>通知島換內容：新內容淡入並從 <see cref="ContentScaleFrom"/> 放大到 1。</summary>
    public const int ContentFadeIn = 180;

    public const double ContentScaleFrom = 0.96;

    /// <summary>成功勾號彈出的總長；ui-guidelines 的狀態回饋上限是 400 ms。</summary>
    public const int Pop = 380;

    /// <summary>失敗短震每一格的長度；共五格，250 ms 內結束。</summary>
    public const int ShakeStep = 50;

    public static TimeSpan Duration(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>通知表面的預設補間：ease-out，從呼叫端給的目前值接續。</summary>
    public static DoubleAnimation Ease(double from, double to, int milliseconds) =>
        new(from, to, Duration(milliseconds)) { EasingFunction = EaseOut };

    private static readonly CubicEase EaseOut = FrozenEaseOut();

    /// <summary>進度圈；只准膠囊或抬頭上的那一個掛它，各列的執行中一律靜態。</summary>
    public static DoubleAnimation Spinner() =>
        new(0, 360, Duration(Spin)) { RepeatBehavior = RepeatBehavior.Forever };

    /// <summary>成功微彈出、失敗短震；其餘狀態不播。結束後交還基底值。</summary>
    public static void PlayResult(ScaleTransform scale, TranslateTransform shake, NotificationVisualStatus state)
    {
        if (state == NotificationVisualStatus.Completed) PlayPop(scale);
        else if (state == NotificationVisualStatus.Failed) PlayShake(shake);
    }

    public static void PlayPop(ScaleTransform scale)
    {
        var pop = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        pop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.65, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.18, KeyTime.FromTimeSpan(Duration(220)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(Duration(Pop))));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    public static void PlayShake(TranslateTransform shake)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        var values = new[] { 0d, -2, 2, -1, 1, 0 };
        for (var i = 0; i < values.Length; i++)
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(values[i], KeyTime.FromTimeSpan(Duration(i * ShakeStep))));
        shake.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    /// <summary>停掉回饋並交還基底值；關掉動畫或表面離開畫面時用。</summary>
    public static void StopResult(ScaleTransform scale, TranslateTransform shake)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        shake.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private static CubicEase FrozenEaseOut()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }
}
