using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 通知島的時長、緩動與狀態回饋；島嶼、列、附條與 Chrome 共用這一份。
/// </summary>
/// <remarks>
/// 散在表面、列與 Chrome 三處各寫一次的版本，改一個數字要找三個地方，而漏掉的那一處
/// 只有在兩處並排時才看得出節奏不一樣。尺寸變形（通知島的寬、高與圓角）走
/// <see cref="SpringMotion"/>，不在這裡：那是可中斷的揭露動畫，不是固定長度的補間。
/// 狀態回饋守 ui-guidelines 的上限：縮放不超過 400 ms、位移不超過 300 ms。
/// </remarks>
internal static class NotificationMotion
{
    /// <summary>表面收場：淡出並縮小；到期判斷（<c>NotificationLifecycle</c>）等的也是這一段。</summary>
    public const int Exit = 220;

    /// <summary>進度條從目前值接續到新的比例。</summary>
    public const int Progress = 320;

    /// <summary>全部結束之後進度條停多久才收成分隔線；太快會看不到它走到底。</summary>
    public const int ProgressSettleDelay = 400;

    /// <summary>進度條收成分隔線，或有新工作時長回來。</summary>
    public const int ProgressSettle = 240;

    /// <summary>抬頭或膠囊上唯一那一個進度圈轉一圈。</summary>
    public const int Spin = 1100;

    /// <summary>通知島換內容：舊內容淡出。</summary>
    public const int ContentFadeOut = 120;

    /// <summary>通知島換內容：新內容晚這麼久才開始，兩份不會同時搶視線。</summary>
    public const int ContentDelay = 60;

    /// <summary>通知島換內容：新內容淡入並從 <see cref="ContentScaleFrom"/> 放大到 1。</summary>
    public const int ContentFadeIn = 180;

    public const double ContentScaleFrom = 0.96;

    /// <summary>展開時各列依序進場，相鄰兩列差這麼多。</summary>
    public const int RowStagger = 30;

    /// <summary>最多錯開幾列；之後的列跟最後一個錯開的同時進場。加上換內容的延遲，整段在 470 ms 內結束。</summary>
    public const int StaggerLimit = 8;

    /// <summary>依序進場時每一列從下方移上來的距離。</summary>
    public const double StaggerShift = 8;

    /// <summary>新列長出高度、淡入並從上方落定。</summary>
    public const int RowEnter = 200;

    /// <summary>新列落定前的位移；只是提示「這是新的」，不是滑入。</summary>
    public const double RowEnterShift = 4;

    /// <summary>列離場：先淡出。</summary>
    public const int RowFadeOut = 120;

    /// <summary>列離場：淡出後收起高度，下面的列補上來。</summary>
    public const int RowCollapse = 160;

    /// <summary>失敗底色、停駐底色淡入。</summary>
    public const int Wash = 100;

    /// <summary>停駐底色淡出；比淡入慢一點，滑鼠掃過時不閃。</summary>
    public const int WashOut = 150;

    /// <summary>成功勾號以筆畫描出。</summary>
    public const int CheckDraw = 220;

    /// <summary>勾號描完之後的微彈；與描繪合計 380 ms，在狀態回饋的 400 ms 上限內。</summary>
    public const int CheckSettle = 160;

    /// <summary>會換字的那一行：舊字上移離開、新字從下面進來。</summary>
    public const int Roll = 180;

    /// <summary>換字時的位移。</summary>
    public const double RollShift = 6;

    /// <summary>失敗短震每一格的長度；共五格，250 ms 內結束。</summary>
    public const int ShakeStep = 50;

    public static TimeSpan Duration(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>通知表面的預設補間：ease-out，從呼叫端給的目前值接續。</summary>
    public static DoubleAnimation Ease(double from, double to, int milliseconds) =>
        new(from, to, Duration(milliseconds)) { EasingFunction = EaseOut };

    /// <summary>
    /// 延遲之後才補間，但延遲期間就已經停在起點。
    /// </summary>
    /// <remarks>
    /// 單用 <see cref="Timeline.BeginTime"/> 的話，開始之前屬性是基底值：依序進場的列會先整份出現，
    /// 輪到它時才跳回 0 再淡入。
    /// </remarks>
    public static DoubleAnimationUsingKeyFrames Delayed(double from, double to, int delay, int milliseconds)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(Duration(delay))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(Duration(delay + milliseconds)), EaseOut));
        return animation;
    }

    private static readonly CubicEase EaseOut = FrozenEaseOut();

    /// <summary>進度圈；只准膠囊、清單抬頭或附條上的那一個掛它，同一時間只有一個。</summary>
    public static DoubleAnimation Spinner() =>
        new(0, 360, Duration(Spin)) { RepeatBehavior = RepeatBehavior.Forever };

    /// <summary>勾號描完之後的微彈；結束後交還基底值。</summary>
    public static void PlaySettle(ScaleTransform scale)
    {
        var settle = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        settle.KeyFrames.Add(new EasingDoubleKeyFrame(1.06, KeyTime.FromTimeSpan(Duration(CheckSettle / 2)), EaseOut));
        settle.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(Duration(CheckSettle))));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
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

    /// <summary>把元素的透明度從目前值接到 <paramref name="to"/>；動畫關著時直接到位。</summary>
    public static void Fade(UIElement element, double to, int milliseconds, bool motion)
    {
        if (!motion) { element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = to; return; }
        var fade = Ease(element.Opacity, to, milliseconds);
        fade.FillBehavior = FillBehavior.Stop;
        element.Opacity = to;
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static CubicEase FrozenEaseOut()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }
}
