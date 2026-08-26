namespace SqlAssist.Core;

/// <summary>結構預覽視窗擺在編輯器的哪裡。</summary>
public enum SqlPreviewPlacement
{
    /// <summary>
    /// 貼在建議清單的左右任一側。
    /// </summary>
    /// <remarks>
    /// 落在哪一側、離清單多遠都由平台計算：它會避開清單已經佔住的空間，
    /// 撞到螢幕邊界就翻到另一邊。清單為自己的說明提示保留的空間也算在內，
    /// 所以視窗有時會被推得比看起來該有的位置更靠外側。
    /// </remarks>
    Beside,

    /// <summary>
    /// 擺在建議清單的上方或下方，從清單錨點延伸到編輯器右側。
    /// </summary>
    /// <remarks>
    /// 位置不再受清單寬度影響，而且一百多個欄位的資料表可以一次攤開好幾欄
    /// 而不必橫向捲動——代價是會蓋住上下幾行程式碼。
    /// </remarks>
    Stacked
}
