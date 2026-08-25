using System;
using System.Runtime.Serialization;

namespace SqlAssist.Core;

/// <summary>
/// 結構預覽視窗的設定。
/// </summary>
/// <remarks>
/// 預設是「按向右鍵才展開」：使用者用方向鍵掃過二十個資料表時，
/// 自動展開等於連續二十次版面重畫與可能的資料庫查詢，
/// 而他其實只想找到名字對的那一個。停下來按一次向右鍵，才是真的想看它。
/// </remarks>
[DataContract]
public sealed class SqlAssistPreviewSettings
{
    /// <summary>視窗允許的最小尺寸，避免使用者把握把拉到看不見。</summary>
    public const int MinimumWidth = 320;

    public const int MinimumHeight = 180;

    public const int MaximumWidth = 2000;

    public const int MaximumHeight = 1400;

    /// <summary>
    /// 設定檔裡的觸發模式名稱。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="SqlAssistSuggestionSettings.Engine"/> 同樣的理由存成字串：
    /// 手動編輯 settings.json 的人看到 0 與 2 無從判斷那是什麼。
    /// </remarks>
    [DataMember(Name = "mode", Order = 1)]
    private string? ModeName { get; set; }

    /// <summary>預覽視窗的觸發方式。無法辨識的值一律當成預設的向右鍵展開。</summary>
    [IgnoreDataMember]
    public SqlPreviewMode Mode
    {
        get
        {
            if (string.Equals(ModeName, "off", StringComparison.OrdinalIgnoreCase))
            {
                return SqlPreviewMode.Off;
            }

            return string.Equals(ModeName, "delay", StringComparison.OrdinalIgnoreCase)
                ? SqlPreviewMode.Delay
                : SqlPreviewMode.RightArrow;
        }

        set => ModeName = value switch
        {
            SqlPreviewMode.Off => "off",
            SqlPreviewMode.Delay => "delay",
            _ => "rightArrow"
        };
    }

    /// <summary>
    /// 延遲模式下，選取要停多久才展開。
    /// </summary>
    /// <remarks>
    /// 這個值同時也是向右鍵模式下「快取沒命中時才去查資料庫」的緩衝：
    /// 展開狀態下繼續用方向鍵移動，仍然不該每一格都送出一次查詢。
    /// </remarks>
    [DataMember(Name = "delayMilliseconds", Order = 2)]
    public int DelayMilliseconds { get; set; } = 220;

    [DataMember(Name = "width", Order = 3)]
    public double Width { get; set; } = 620;

    [DataMember(Name = "height", Order = 4)]
    public double Height { get; set; } = 420;

    /// <summary>設定檔裡的擺放位置名稱；與 <see cref="ModeName"/> 同樣的理由存成字串。</summary>
    [DataMember(Name = "placement", Order = 5)]
    private string? PlacementName { get; set; }

    /// <summary>視窗擺在哪裡。無法辨識的值一律當成預設的貼在清單旁。</summary>
    [IgnoreDataMember]
    public SqlPreviewPlacement Placement
    {
        get => string.Equals(PlacementName, "stacked", StringComparison.OrdinalIgnoreCase)
            ? SqlPreviewPlacement.Stacked
            : SqlPreviewPlacement.Beside;

        set => PlacementName = value switch
        {
            SqlPreviewPlacement.Stacked => "stacked",
            _ => "beside"
        };
    }

    /// <summary>把尺寸收斂到允許範圍內；設定檔被手動改壞時不至於畫出看不見的視窗。</summary>
    public double ClampWidth() => Clamp(Width, MinimumWidth, MaximumWidth, 620);

    public double ClampHeight() => Clamp(Height, MinimumHeight, MaximumHeight, 420);

    /// <summary>延遲毫秒數的合理範圍；0 代表不緩衝，超過兩秒則失去意義。</summary>
    public int ClampDelay() => DelayMilliseconds < 0 ? 0 : Math.Min(DelayMilliseconds, 2000);

    private static double Clamp(double value, double minimum, double maximum, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return fallback;
        }

        return Math.Min(Math.Max(value, minimum), maximum);
    }

    public SqlAssistPreviewSettings Clone()
    {
        return new SqlAssistPreviewSettings
        {
            Mode = Mode,
            Placement = Placement,
            DelayMilliseconds = DelayMilliseconds,
            Width = Width,
            Height = Height
        };
    }
}
