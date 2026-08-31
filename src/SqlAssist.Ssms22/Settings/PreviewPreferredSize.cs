using SqlAssist.Core.Settings;

namespace SqlAssist.Ssms22.Settings;

/// <summary>
/// 一種擺放方向記住的視窗尺寸。
/// </summary>
/// <remarks>
/// 上下與側邊分開保存，而且側邊放不下退回上下時要換成另一組——把兩個數值綁在一起
/// 傳，是為了讓「換一組」變成換一個值，而不是每個呼叫端各自記得要換兩個參數。
/// </remarks>
internal readonly struct PreviewPreferredSize
{
    public PreviewPreferredSize(double? width, double height)
    {
        Width = width;
        Height = height;
    }

    /// <summary>null 代表尚未手動調寬，寬度採「延伸到編輯器右側」的自動值。</summary>
    public double? Width { get; }

    public double Height { get; }

    public double WidthOrDefault => Width ?? SqlAssistLimits.DefaultPreviewWidth;
}
