namespace SqlAssist.Core.Settings;

/// <summary>結構預覽視窗擺在編輯器的哪裡。</summary>
public enum SqlPreviewPlacement
{
    /// <summary>
    /// 貼在建議清單的左右任一側。
    /// </summary>
    /// <remarks>
    /// 優先右側，再試左側；兩側都放不下時退回上下擺放。
    /// </remarks>
    Beside,

    /// <summary>
    /// 擺在建議清單的上方或下方，從清單錨點延伸到編輯器右側。
    /// </summary>
    /// <remarks>
    /// 優先下方，再試上方；垂直可用範圍包含同一查詢文件的結果窗格。
    /// 欄位很多的資料表可以一次攤開好幾欄，代價是會覆蓋部分文件內容。
    /// </remarks>
    Stacked
}
