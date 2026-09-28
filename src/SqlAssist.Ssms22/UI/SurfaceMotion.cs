using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 浮在編輯器上的表面（通知島、浮動預覽）共用的進出場節奏。
/// </summary>
/// <remarks>
/// 兩個表面是使用者會同時看到的兩個浮層，節奏必須是同一份：外形先長，內容晚一點才淡入並從
/// <see cref="ContentScaleFrom"/> 放大；收起時內容先淡出，外形再縮回。內容跟著外形一路被
/// 裁切露出的那一版，一整片資料格像從門縫裡擠出來——外形還在長的時候眼睛沒有東西可讀，
/// 讓內容晚到，看到的就是「島長開了，東西放上去」。
///
/// 外形本身走 <see cref="SpringMotion"/>（可中斷、保留速度）；這裡是內容那一層固定長度的補間。
/// 通知專屬的列、進度與回饋節奏在 <see cref="NotificationMotion"/>。
/// </remarks>
internal static class SurfaceMotion
{
    /// <summary>內容淡出；收起與換內容時舊的那一份都走這個長度。</summary>
    public const int ContentFadeOut = 120;

    /// <summary>新內容晚這麼久才開始，外形先長一段，兩份不會同時搶視線。</summary>
    public const int ContentDelay = 60;

    /// <summary>新內容淡入並從 <see cref="ContentScaleFrom"/> 放大到 1。</summary>
    public const int ContentFadeIn = 180;

    public const double ContentScaleFrom = 0.96;

    public static TimeSpan Duration(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>浮層的預設補間：ease-out，從呼叫端給的目前值接續。</summary>
    public static DoubleAnimation Ease(double from, double to, int milliseconds) =>
        new(from, to, Duration(milliseconds)) { EasingFunction = EaseOut };

    /// <summary>
    /// 內容進場：晚 <see cref="ContentDelay"/> 才淡入，同時從 <see cref="ContentScaleFrom"/> 放大。
    /// </summary>
    /// <remarks>
    /// 透明度的基底值設成 0、動畫保留結束值：單用 <see cref="Timeline.BeginTime"/> 而基底值是 1 的話，
    /// 延遲那 60 ms 內容會先整份出現，輪到它時才跳回 0 再淡入。要交還基底值的一方自己
    /// 清掉動畫並設回 1。縮放結束後交還基底值，之後的版面不帶著變換。
    /// </remarks>
    public static void EnterContent(UIElement content, ScaleTransform? scale)
    {
        var delay = Duration(ContentDelay);
        var appear = Ease(0, 1, ContentFadeIn);
        appear.BeginTime = delay;
        content.Opacity = 0;
        content.BeginAnimation(UIElement.OpacityProperty, appear);
        if (scale is null)
        {
            return;
        }

        var grow = Ease(ContentScaleFrom, 1, ContentFadeIn);
        grow.BeginTime = delay;
        grow.FillBehavior = FillBehavior.Stop;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>內容從目前畫面上的透明度淡出；先清動畫會退回基底值，淡入到一半的那一份會先閃一下。</summary>
    public static DoubleAnimation ExitContent(UIElement content)
    {
        var fade = Ease(content.Opacity, 0, ContentFadeOut);
        content.BeginAnimation(UIElement.OpacityProperty, fade);
        return fade;
    }

    /// <summary>停掉內容的進出場並交還完整顯示；動畫關著或表面要立刻收完時用。</summary>
    public static void ResetContent(UIElement content, ScaleTransform? scale)
    {
        content.BeginAnimation(UIElement.OpacityProperty, null);
        content.Opacity = 1;
        scale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    /// <summary>浮層共用的 ease-out；關鍵影格也用這一條，節奏才對得上。</summary>
    public static readonly CubicEase EaseOut = FrozenEaseOut();

    private static CubicEase FrozenEaseOut()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }
}
