using System;

namespace SqlAssist.Core;

/// <summary>
/// 每一項數值設定的合理範圍，以及把界外值收斂回範圍內的規則。
/// </summary>
/// <remarks>
/// Unified Settings 的 <c>minimum</c> / <c>maximum</c> 只約束設定 UI 上的輸入，
/// 手動編輯設定檔或讀不到註冊資訊時仍可能拿到界外值，
/// 因此讀取端一律再收斂一次——這裡是那一份規則的唯一出處。
/// </remarks>
public static class SqlAssistLimits
{
    public const int MinimumTriggerCharacters = 1;

    public const int MaximumTriggerCharacters = 10;

    public const int DefaultTriggerCharacters = 1;

    /// <summary>
    /// 展開萬用字元時，一行最多排到多寬。
    /// </summary>
    /// <remarks>
    /// 排法本身是設定（<see cref="SqlWildcardLayout"/>），這個寬度刻意不是：
    /// 使用者感覺得到的是「一行還是好幾行」，而那個分界點落在 118 還是 124
    /// 他不會有意見。訂在 120 是因為多數查詢視窗一眼看得完的就是這個寬度，
    /// 再寬就要橫向捲動。
    ///
    /// <see cref="SqlWildcardLayout.OnePerLine"/> 下沒有作用。
    /// </remarks>
    public const int MaximumWildcardLineWidth = 120;

    public const int MinimumPreviewDelay = 0;

    /// <summary>超過兩秒的延遲等於不會展開，再往上調沒有意義。</summary>
    public const int MaximumPreviewDelay = 2000;

    public const int DefaultPreviewDelay = 220;

    /// <summary>字級的合理範圍；再小讀不到，再大一列就放不下幾個字。</summary>
    public const double MinimumPreviewFontSize = 9;

    public const double MaximumPreviewFontSize = 20;

    public const double DefaultPreviewFontSize = 14;

    /// <summary>視窗允許的最小尺寸，避免使用者把握把拉到看不見。</summary>
    public const double MinimumPreviewWidth = 320;

    public const double MaximumPreviewWidth = 2000;

    public const double DefaultPreviewWidth = 620;

    public const double MinimumPreviewHeight = 180;

    public const double MaximumPreviewHeight = 1400;

    public const double DefaultPreviewHeight = 420;

    public static int ClampTriggerCharacters(int value) =>
        Clamp(value, MinimumTriggerCharacters, MaximumTriggerCharacters);

    public static int ClampPreviewDelay(int value) =>
        Clamp(value, MinimumPreviewDelay, MaximumPreviewDelay);

    public static double ClampPreviewFontSize(double value) =>
        Clamp(value, MinimumPreviewFontSize, MaximumPreviewFontSize, DefaultPreviewFontSize);

    public static double ClampPreviewWidth(double value) =>
        Clamp(value, MinimumPreviewWidth, MaximumPreviewWidth, DefaultPreviewWidth);

    public static double ClampPreviewHeight(double value) =>
        Clamp(value, MinimumPreviewHeight, MaximumPreviewHeight, DefaultPreviewHeight);

    private static int Clamp(int value, int minimum, int maximum) =>
        Math.Min(Math.Max(value, minimum), maximum);

    /// <summary>
    /// 收斂浮點數值。
    /// </summary>
    /// <remarks>
    /// NaN、無限大與零以下都當成「這個值壞了」而回退到預設，
    /// 不是收斂到下限：0 或 NaN 不是使用者調出來的，是資料損壞。
    /// </remarks>
    private static double Clamp(double value, double minimum, double maximum, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return fallback;
        }

        return Math.Min(Math.Max(value, minimum), maximum);
    }
}
