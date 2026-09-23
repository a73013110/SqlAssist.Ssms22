using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SqlAssist.Core.Connections;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.UI;

/// <summary>圖示鈕、圖示開關、展開／收合箭頭與分隔線：任何一個工具窗、面板或對話框都用得到的 primitive。</summary>
/// <remarks>
/// 與 <c>SqlAssistChrome.Rows.cs</c> 同一個理由放在中性的 partial：這幾顆原本住在
/// <c>SqlAssistChrome.SqlMemory.cs</c> 與 <c>SqlAssistChrome.Filters.cs</c>，而它們與 SQL Memory
/// 或篩選的領域語意一點關係都沒有——箭頭的呼叫端是主從區把手與過濾面板，圖示鈕的呼叫端遍布
/// 兩個工具窗、預覽與版本歷史，分隔線同時分篩選列上的幾群與預覽工具列上的導覽與命令。
/// 檔名說「這是 Memory 的」或「這是篩選的」會讓下一個視窗去那一份取，或者再造一顆自己的。
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

    /// <summary>
    /// 圖示鈕的開關版；外觀與 <see cref="CreateIconButton"/> 相同，多一個「現在開著」的樣子。
    /// </summary>
    /// <remarks>
    /// 一顆按鈕做的是一次動作（複製、重新整理），做完就沒事了；換行、只看差異這一種按下去
    /// 之後<b>一直維持</b>的東西是狀態，而狀態要在那一顆自己身上看得出來。畫成按鈕的那一版，
    /// 使用者按完換行再回頭看工具列，分不出現在是開著還是自己按錯了，只能去看內容猜——
    /// 而一份本來就不長的指令碼看不出差別。
    ///
    /// 樣式與搜尋框裡那兩顆開關共用 <see cref="CreateToggleStyle"/>：兩處畫成不同的「開著」，
    /// 使用者要學兩次同一件事。按下狀態另由 <see cref="System.Windows.Automation.TogglePattern"/>
    /// 唸得出來，不只靠顏色。
    /// </remarks>
    public static ToggleButton CreateIconToggle(SqlIcon icon, string label)
    {
        var toggle = new ToggleButton
        {
            Content = CreateIcon(icon),
            Style = CreateToggleStyle(),
            Padding = new Thickness(5),
            MinWidth = 26,
            MinHeight = 26,
            ToolTip = label
        };
        AutomationProperties.SetName(toggle, label);
        return toggle;
    }

    /// <summary>
    /// 範圍列最左邊那一顆：把範圍換成查詢視窗的連線。SQL Search 與 SQL Memory 共用。
    /// </summary>
    /// <param name="target">
    /// 按下去會套到哪一條連線；Tooltip 打開那一刻才問一次，沒有連線時回 null。
    /// 呼叫端負責包平台防護，這一層只畫。
    /// </param>
    /// <remarks>
    /// 圖示鈕而不是帶文字的按鈕：範圍列在停靠面板裡本來就擠，字留在 Tooltip 與自動化名稱裡。
    /// Tooltip 不訂閱換分頁或換連線的事件：只有使用者停在這一顆上的那一刻需要答案，
    /// 一直跟著更新等於為一句沒有人在看的字接兩條事件。
    ///
    /// 按下去做什麼由宿主決定，兩邊不同但結果一致——範圍就是查詢視窗那條連線：
    /// SQL Memory 把當下的伺服器與資料庫寫進篩選；SQL Search 改回跟著查詢視窗，並清掉指名的資料庫。
    /// </remarks>
    public static Button CreateEditorConnectionButton(Func<SqlConnectionLabel?> target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        var button = CreateIconButton(SqlIcon.Connection, SqlEditorConnectionText.ApplyAction);
        button.ToolTipOpening += (_, _) => button.ToolTip = SqlEditorConnectionText.ApplyToolTip(target());
        return button;
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

    /// <summary>兩群之間那一條分隔線的高度；比按鈕矮一截，讀起來是一條界線不是一個邊框。</summary>
    private const double GroupDividerHeight = 18d;

    /// <summary>分隔線左右各留的間距；群<b>內</b>只有 4 DIP，兩個數字的差就是「這是兩群」。</summary>
    private const double GroupDividerGap = 6d;

    /// <summary>群<b>內</b>那一條的高度；比群間矮一截，兩條並排時分得出哪一條是界線。</summary>
    private const double ItemDividerHeight = 10d;

    /// <summary>群內分隔線左右各留的間距；與沒有線的那一版同一個數字，加線不改版面密度。</summary>
    private const double ItemDividerGap = 4d;

    /// <summary>群內那一條的濃度；髮絲線本來就淡，再降一階才不會讀成群界。</summary>
    internal const double ItemDividerOpacity = 0.55d;

    /// <summary>
    /// 工具列上兩<b>群</b>控制項之間的淡色分隔線。
    /// </summary>
    /// <remarks>
    /// 分群的規則只有一條：回答<b>同一個問題</b>的控制項是一群。
    /// SQL Memory 的「狀態」「期間」與連線是三群，SQL Search 第二層的「搜哪裡（伺服器、資料庫）」、
    /// 「搜什麼（種類）」與「比對哪裡（名稱／內容／欄位）」是三群。
    ///
    /// 沒有這條線時，一排按鈕看起來是同一組可以互相取代的選項，而使用者會去找一顆「全部」
    /// 把它們一起關掉；全靠加大間距分群的那一版在窄窗收完字之後就分不出來了，
    /// 因為那時候每一顆本來就只剩一個圖示。
    ///
    /// 群<b>內</b>另有一條矮一截、淡一階的 <see cref="CreateItemDivider"/>：每一顆
    /// 之間都看得到界線，而兩級的高度與間距差讓分群仍然讀得出來。兩條畫成同一種的那一版
    /// 等於把分群取消掉，使用者會把「種類」讀成第三個範圍條件。
    ///
    /// 不只篩選列用它：預覽工具列上「上一處／第幾處／下一處」與命令那兩群也是同一條線。
    /// 一條給篩選、一條給工具列的那一版，兩處的高度與濃度會各自漂移，而使用者讀到的是
    /// 兩種不同強度的分群。
    ///
    /// 線本身不表達狀態，所以用 <see cref="ThemeBrush.Hairline"/> 而不是任何語意色，
    /// 也不隨停駐或選取改變；<see cref="UIElement.SnapsToDevicePixels"/> 讓它在 150% DPI 下
    /// 仍然是實心的一條，不糊成兩條半透明的。
    /// </remarks>
    public static Border CreateGroupDivider() =>
        CreateDivider(GroupDividerHeight, GroupDividerGap, opacity: 1d);

    /// <summary>同一群裡兩顆之間那一條；矮一截、淡一階，間距仍是群內的 4 DIP。</summary>
    /// <remarks>理由與兩級的分工見 <see cref="CreateGroupDivider"/>。</remarks>
    public static Border CreateItemDivider() =>
        CreateDivider(ItemDividerHeight, ItemDividerGap, ItemDividerOpacity);

    private static Border CreateDivider(double height, double gap, double opacity)
    {
        var divider = new Border
        {
            Width = 1,
            Height = height,
            Margin = new Thickness(gap, 0, gap, 0),
            Opacity = opacity,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
            Focusable = false
        };
        divider.SetResourceReference(Border.BackgroundProperty, ThemeBrush.Hairline);
        // 分隔線只是視覺上的分群，不唸出來：每一群的名稱已經說得出同一件事。
        // WPF 不會替沒有內容也不可聚焦的 Border 產生自動化節點，所以這裡不必再壓一次。
        return divider;
    }
}
