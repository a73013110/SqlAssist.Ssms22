using System.Windows.Media;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 指令碼表面的配色取捨；不碰 SSMS 服務，只做色彩運算，因此在測試裡跑的就是產品這一份。
/// </summary>
/// <remarks>
/// 兩個決定要放在一起看：<b>先選定畫在哪一個底色上</b>，才談分類色讀不讀得到。
/// 把編輯器的分類色配上工具窗的底色，等於把為 A 表面調好的顏色搬到 B 表面——
/// 同樣是深色但色系不同的主題（月光、神秘森林、辣紅）下對比未必過得了關，
/// 而「過不了就整個換成前景色」會讓四種分類變成同一個顏色，使用者看到的就是高亮不見了。
/// </remarks>
internal static class ScriptPalette
{
    private const double TextContrast = 4.5;

    /// <summary>
    /// 指令碼要畫在哪一組底色與前景上。
    /// </summary>
    /// <param name="editor">
    /// 編輯器 Fonts and Colors 的那一組，<b>成對</b>傳入，缺一個就傳 null。有查詢視窗時來自那個檢視，
    /// 沒有時來自「Plain Text」那一格——兩者是同一份設定，使用者之後打開查詢視窗才不會看到換一套顏色。
    /// </param>
    /// <param name="shell">工具窗自己那一組；編輯器那一組取不到、或它自己就讀不到時的退路。</param>
    public static (Color Background, Color Foreground) Surface(
        (Color Background, Color Foreground)? editor, (Color Background, Color Foreground) shell)
        => editor is { } pair && ThemeColorMath.Contrast(pair.Foreground, pair.Background) >= TextContrast
            ? pair
            : shell;

    /// <summary>
    /// 分類色；對比不足時朝可讀的方向調整，<b>不</b>換成前景色。
    /// </summary>
    /// <remarks>
    /// 調整保留色相，關鍵字、註解、字串與數值因此仍然彼此分得開；換成前景色則是四種全部相同，
    /// 那正是使用者眼中的「沒有高亮」。只有真的問不到顏色時才退回前景。
    /// </remarks>
    public static Color Classification(Color? candidate, Color foreground, Color background)
        => candidate is { } color ? ThemeColorMath.EnsureTextContrast(color, background) : foreground;
}
