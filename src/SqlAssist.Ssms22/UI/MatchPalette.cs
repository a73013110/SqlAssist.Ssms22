using System.Windows.Media;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 搜尋命中的整組顏色：一般命中與「目前停在的那一處」各一對底色與字色。
/// </summary>
/// <remarks>
/// 顯示命中的表面各自給自己的底色當基準（工具窗是清單底色，指令碼借的是 SSMS 編輯器底色，
/// 兩者在深色主題下不一定同深淺），推導只有這一份。各寫一份的下場是同一個搜尋字在同一個
/// 視窗的兩塊看起來不是同一件事。
///
/// 底色怎麼調、字色往哪一端走、兩級怎麼分，全部在 <see cref="TextMarkColors"/>；這裡只負責
/// 說「命中有兩級、哪一級配哪一個強度」，以及高對比下換成哪一組系統色。
///
/// <b>兩級都蓋掉底下的語法著色。</b>把一般命中做成「淡到讓語法著色仍讀得出來」的那一版，等於
/// 要求底色與最淡的那個分類色維持 4.5:1，而校正只能把底色往表面推——推完的結果就是一層幾乎
/// 看不見的薄色，使用者得瞇著眼找命中在哪裡。留住著色與一眼看得出來互斥，而「有沒有標出來」
/// 才是使用者要從這裡讀到的事；分類色在命中那幾個字上讀不到並不影響判讀，它們的周圍仍然有色。
/// </remarks>
internal readonly struct MatchPalette
{
    private MatchPalette(Color background, Color foreground, Color currentBackground, Color currentForeground)
    {
        Background = background;
        Foreground = foreground;
        CurrentBackground = currentBackground;
        CurrentForeground = currentForeground;
    }

    /// <summary>一般命中的底色。</summary>
    public Color Background { get; }

    /// <summary>一般命中的字色。</summary>
    public Color Foreground { get; }

    /// <summary>目前停在那一處的底色；整片指令碼裡要一眼跳出來。</summary>
    public Color CurrentBackground { get; }

    /// <summary>目前停在那一處的字色。</summary>
    public Color CurrentForeground { get; }

    /// <param name="accent">主題強調色。</param>
    /// <param name="surface">這個表面自己的底色。</param>
    /// <param name="foreground">這個表面自己的前景色；讀得到就留著，讀不到才換。</param>
    /// <param name="selection">高對比下的系統選取色與配對文字。</param>
    public static MatchPalette Create(
        Color accent, Color surface, Color foreground, bool highContrast,
        (Color Background, Color Foreground) selection)
    {
        // 高對比沒有中間色可調：一般命中用反白，目前那一處用系統選取色。兩者都是實色、都合規，
        // 而且差的是色相不是明度——明度在那裡本來就只有兩級。
        if (highContrast) return new MatchPalette(foreground, surface, selection.Background, selection.Foreground);

        var (current, match) = TextMarkColors.Pair(accent, surface);
        return new MatchPalette(
            match, TextMarkColors.Ink(match, foreground),
            current, TextMarkColors.Ink(current, foreground));
    }
}
