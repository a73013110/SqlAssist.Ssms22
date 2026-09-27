using System;
using System.Windows.Media;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 文字選取的顏色：蓋在字<b>上面</b>的一層半透明色，濃度全在 alpha 裡。
/// </summary>
/// <remarks>
/// .NET Framework 的 WPF 把選取畫在文字上方的裝飾層，字色不會換；<c>SelectionTextBrush</c> 只在
/// 另一種繪製模式生效，而那是整個行程一個開關，在 SSMS 裡不能動。所以這一層同時染了底與字，
/// 規則只有一條：從 <see cref="MaximumOpacity"/> 起，取仍讓表面前景過 4.5:1 的最濃那一級。
///
/// 不與列的選取（<see cref="ThemeBrush.RowSelected"/>）共用：列的底色畫在字<b>底下</b>，本來就淡；
/// 借來當文字選取還會被 <c>SelectionOpacity</c>（預設 0.4）再乘一次，12% 剩 5%，
/// 跟滑鼠停駐分不出來。
/// </remarks>
internal static class TextSelectionColors
{
    /// <summary>覆蓋層的濃度上限。</summary>
    /// <remarks>
    /// 取原生 WPF 文字框的預設濃度，看起來與 SSMS 其他文字框一樣重；再濃下去，被選的字開始褪進底色。
    /// 控制項的 <c>SelectionOpacity</c> 因此固定為 1，濃度只在這裡算一次。
    /// </remarks>
    public const double MaximumOpacity = 0.4;

    /// <summary>
    /// 這個表面上的選取覆蓋色：一般主題用強調色，高對比用系統選取色，兩者走同一條濃度規則。
    /// </summary>
    /// <param name="foreground">表面的前景；必須已合成成不透明色。</param>
    public static Color Create(Color accent, bool highContrast, Color highlight, Color surface, Color foreground)
    {
        var seed = highContrast ? highlight : accent;
        var alpha = (byte)Math.Round(seed.A * MaximumOpacity);

        // 覆蓋層把字與底往同一個顏色拉，對比只會變低；從上限往下找第一個仍讀得到的濃度。
        while (alpha > 0 && !Readable(Color.FromArgb(alpha, seed.R, seed.G, seed.B)))
        {
            alpha--;
        }

        return Color.FromArgb(alpha, seed.R, seed.G, seed.B);

        bool Readable(Color overlay) => ThemeColorMath.Contrast(
            ThemeColorMath.Composite(overlay, foreground),
            ThemeColorMath.Composite(overlay, surface)) >= 4.5;
    }
}
