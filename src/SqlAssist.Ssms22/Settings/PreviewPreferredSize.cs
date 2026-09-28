using SqlAssist.Core.Settings;

namespace SqlAssist.Ssms22.Settings;

/// <summary>
/// 錨在名稱上的預覽要開多大；使用者拖出來的就是它。
/// </summary>
/// <remarks>
/// 釘住的視窗不讀也不寫這一份：那是擺在某個地方的一扇窗，大小屬於那一扇。拖一扇釘住的
/// 窄窗就讓之後每一次預覽都變窄，使用者分不出是哪一次拖出來的。
/// </remarks>
internal readonly struct PreviewPreferredSize
{
    public PreviewPreferredSize(double? width, double height)
    {
        Width = width;
        Height = height;
    }

    public static PreviewPreferredSize Default { get; } = new(null, SqlAssistLimits.DefaultPreviewHeight);

    /// <summary>null 代表尚未手動調寬，寬度採「延伸到編輯器右側」的自動值。</summary>
    public double? Width { get; }

    public double Height { get; }

    public double WidthOrDefault => Width ?? SqlAssistLimits.DefaultPreviewWidth;
}
