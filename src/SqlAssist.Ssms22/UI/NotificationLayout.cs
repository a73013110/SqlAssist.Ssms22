using System.Windows;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 通知島裡所有內容共用的對齊基準（DIP）。
/// </summary>
/// <remarks>
/// 抬頭、文件列、各列、提醒卡與附條各自決定內距的版本，圖示差 2 DIP、文字差 4 DIP，並排時一眼就看得出
/// 參差。這裡只有一條圖示中線與一條文字起點：16 DIP 的語意圖示與 12 DIP 的狀態圖示都置中在
/// <see cref="IconCenter"/>，文字一律從 <see cref="TextStart"/> 開始；右側的文字、徽章與叉號的筆畫收在
/// <see cref="TextEnd"/>，按鈕外框收在 <see cref="Right"/>。
/// </remarks>
internal static class NotificationLayout
{
    /// <summary>內容距離左緣。</summary>
    public const double Left = 12;

    /// <summary>按鈕外框距離右緣。</summary>
    public const double Right = 10;

    /// <summary>內容距離上緣；抬頭列的中線因此落在 <see cref="Top"/> + <see cref="HeaderHeight"/> / 2。</summary>
    public const double Top = 8;

    /// <summary>抬頭列（清單的摘要、提醒的標題）的高度；圖示、文字與叉號都在這一列垂直置中。</summary>
    public const double HeaderHeight = 24;

    /// <summary>圖示欄寬：從 <see cref="Left"/> 起算，16 DIP 語意圖示剛好佔滿、再留 4 DIP。</summary>
    public const double IconColumn = 20;

    /// <summary>語意圖示的寬度；<see cref="IconCenter"/> 以它為準。</summary>
    public const double SemanticIcon = 16;

    /// <summary>圖示中線與左緣的距離。</summary>
    public const double IconCenter = Left + SemanticIcon / 2;

    /// <summary>文字起點與左緣的距離。</summary>
    public const double TextStart = Left + IconColumn;

    /// <summary>叉號外框的邊長；筆畫 10 DIP 置中，點擊區比筆畫大一圈。</summary>
    public const double CloseButton = 22;

    /// <summary>右側文字與叉號筆畫的收邊與右緣的距離。</summary>
    public const double TextEnd = Right + (CloseButton - 10) / 2;

    /// <summary>列與附條停駐底色往左右延伸的量；底色變寬，文字位置不動。</summary>
    public const double Bleed = 6;

    /// <summary>12 DIP 狀態圖示放進圖示欄時的左距，讓它與 16 DIP 語意圖示同一條中線。</summary>
    public static Thickness StatusIconInset => new((SemanticIcon - NotificationStatusIcon.Size) / 2, 0, 0, 0);
}
