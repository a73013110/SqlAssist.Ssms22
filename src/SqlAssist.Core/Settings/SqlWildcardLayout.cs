namespace SqlAssist.Core.Settings;

/// <summary>
/// 展開萬用字元之後，那一份欄位清單怎麼排。
/// </summary>
/// <remarks>
/// 三種模式的差別只在「整份放不下一行」的時候：
/// <see cref="OneLineWhenShort"/> 與 <see cref="FillWidth"/> 在放得下時是同一個結果，
/// <see cref="OneLineWhenShort"/> 與 <see cref="OnePerLine"/> 在放不下時是同一個結果。
/// 只有 <see cref="FillWidth"/> 會排出「一行多個欄位」這種形狀。
/// </remarks>
public enum SqlWildcardLayout
{
    /// <summary>
    /// 永遠每欄一行。
    /// </summary>
    /// <remarks>
    /// 唯一一個輸出形狀只由欄位數量決定的模式——換行位置不受欄位名稱長度與
    /// <c>*</c> 的縮排位置影響，所以新增或移除一個欄位只會動到一行。
    /// </remarks>
    OnePerLine,

    /// <summary>整份放得下就排成一行，放不下才每欄一行。</summary>
    OneLineWhenShort,

    /// <summary>
    /// 整份放得下就排成一行，放不下則依行寬排滿，一行放多個欄位。
    /// </summary>
    /// <remarks>
    /// 垂直空間最省，代價是換行位置由前面欄位名稱的字元數決定：
    /// 改一個欄位名，後面的排法會整段跟著變。
    /// </remarks>
    FillWidth
}
