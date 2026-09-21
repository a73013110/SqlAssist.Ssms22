using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SqlAssist.Ssms22.UI;

/// <summary>圖示鈕與展開／收合箭頭：任何一個工具窗、面板或對話框都用得到的兩顆 primitive。</summary>
/// <remarks>
/// 與 <c>SqlAssistChrome.Rows.cs</c> 同一個理由放在中性的 partial：這兩顆原本住在
/// <c>SqlAssistChrome.SqlMemory.cs</c>，而它們與 SQL Memory 的領域語意一點關係都沒有——
/// 箭頭的呼叫端是主從區把手與過濾面板，圖示鈕的呼叫端遍布兩個工具窗、預覽與版本歷史。
/// 檔名說「這是 Memory 的」會讓下一個視窗去 Memory 那一份取，或者再造一顆自己的。
/// </remarks>
internal static partial class SqlAssistChrome
{
    private static readonly Geometry ChevronGeometry = Frozen(Geometry.Parse("M 2,5 L 8,11 14,5"));

    private static Geometry Frozen(Geometry geometry) { geometry.Freeze(); return geometry; }

    public static SqlIconImage CreateIcon(SqlIcon icon) => new() { Icon = icon };

    /// <summary>展開／收合箭頭；屬於控制項外觀而非語意圖示，所以畫向量並跟隨所屬控制項前景。</summary>
    /// <remarks>旋轉只改繪圖、不改量測；幾何以 16 DIP 畫布中心對稱，收合時不推動旁邊文字。</remarks>
    public static Path CreateChevron(bool expanded = true)
    {
        var chevron = new Path
        {
            Data = ChevronGeometry, Width = 16, Height = 16, Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, StrokeThickness = 1.3,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(ChevronAngle(expanded))
        };
        chevron.SetBinding(Shape.StrokeProperty, OwnerForeground());
        return chevron;
    }

    /// <summary>收合是朝右（−90°），展開是朝下（0°）；兩處各寫一次角度的話會轉錯邊。</summary>
    private static double ChevronAngle(bool expanded) => expanded ? 0 : -90;

    /// <summary>箭頭轉向的長度；揭露動畫這一級，短到可以在使用者連按兩下時中途反向。</summary>
    internal static readonly System.TimeSpan ChevronTurnDuration = System.TimeSpan.FromMilliseconds(140);

    /// <summary>
    /// 把一顆已經在畫面上的箭頭轉到展開或收合的方向。
    /// </summary>
    /// <remarks>
    /// 面板、下拉與預覽把手共用這一份：每個呼叫端自己寫一次角度與動畫的下場是其中一邊
    /// 是瞬間跳的，而使用者看得出那兩顆箭頭不是同一種東西。
    ///
    /// 從<b>目前角度</b>轉過去（不指定 <c>From</c>），所以連按兩下時是從轉到一半的位置反向，
    /// 不是先跳回起點再轉。<see cref="FillBehavior.HoldEnd"/> 保持結束值——這是一個狀態，
    /// 不是一次回饋，動畫停了箭頭仍要指著現在的方向。動畫關閉時直接寫角度。
    /// </remarks>
    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    public static void SetChevronExpanded(Path chevron, bool expanded, bool? motion = null)
    {
        if (chevron.RenderTransform is not RotateTransform rotation) return;

        var angle = ChevronAngle(expanded);
        if (!(motion ?? MotionEnabled))
        {
            rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            rotation.Angle = angle;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            To = angle, Duration = ChevronTurnDuration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd
        });
    }

    public static Button CreateIconButton(SqlIcon icon, string label, SqlActionTone tone = SqlActionTone.Neutral)
    {
        var button = CreateButton("", DefaultMetrics);
        button.Content = CreateIcon(icon); button.ToolTip = label;
        button.Padding = new Thickness(5); button.MinWidth = 26; button.MinHeight = 26;
        if (tone != SqlActionTone.Neutral) button.Template = CreateGhostButtonTemplate(tone);
        AutomationProperties.SetName(button, label);
        return button;
    }
}
