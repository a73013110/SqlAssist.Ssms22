using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SqlAssist.Core;

namespace SqlAssist.Ssms22.Options;

/// <summary>「工具 → 選項 → SqlAssist → 結構預覽」頁。</summary>
[ComVisible(true)]
[Guid(PageGuidString)]
public sealed class PreviewOptionsPage : SqlAssistOptionsPage
{
    public const string PageGuidString = "9f2a4c17-6b3e-4d58-9a21-0c5e7b41d8f3";

    [Category("觸發")]
    [DisplayName("顯示時機")]
    [Description(
        "RightArrow：建議清單開著時按向右鍵才展開，展開後跟著選取移動，向左鍵收合。" +
        "Delay：選取停在同一項超過延遲毫秒數就自動展開。" +
        "Off：完全不顯示浮動預覽，改用平台內建的精簡說明提示。")]
    public SqlPreviewMode Mode { get; set; } = SqlPreviewMode.RightArrow;

    [Category("觸發")]
    [DisplayName("延遲毫秒數")]
    [Description(
        "Delay 模式下停留多久才展開；同時也是展開後換選取時「多久之後才去查資料庫」的緩衝。" +
        "有效範圍 0 到 2000。")]
    public int DelayMilliseconds { get; set; } = 220;

    [Category("視窗")]
    [DisplayName("擺放位置")]
    [Description(
        "Beside：貼在建議清單的左右任一側，由平台計算落點與方向。" +
        "Stacked：擺在游標所在行的上下方，寬度吃滿編輯器，" +
        "欄位很多的資料表可以一次攤開而不必橫向捲動，代價是會蓋住上下幾行程式碼。")]
    public SqlPreviewPlacement Placement { get; set; } = SqlPreviewPlacement.Beside;

    [Category("視窗")]
    [DisplayName("寬度")]
    [Description(
        "預覽視窗的寬度，也會在拖曳右下角握把後自動更新。" +
        "擺放位置為 Stacked 時寬度改由編輯器決定，這個值不生效。")]
    public int Width { get; set; } = 620;

    [Category("視窗")]
    [DisplayName("高度")]
    [Description("預覽視窗的高度，也會在拖曳右下角握把後自動更新。")]
    public int Height { get; set; } = 420;

    private protected override void LoadFrom(SqlAssistSettings settings)
    {
        Mode = settings.Preview.Mode;
        Placement = settings.Preview.Placement;
        DelayMilliseconds = settings.Preview.DelayMilliseconds;
        Width = (int)settings.Preview.ClampWidth();
        Height = (int)settings.Preview.ClampHeight();
    }

    private protected override void ApplyTo(SqlAssistSettings settings)
    {
        settings.Preview.Mode = Mode;
        settings.Preview.Placement = Placement;

        // 屬性方格允許輸入任意整數，界限在這裡收斂，讓設定檔永遠是可用值。
        settings.Preview.DelayMilliseconds = Clamp(DelayMilliseconds, 0, 2000);
        settings.Preview.Width = Clamp(
            Width,
            SqlAssistPreviewSettings.MinimumWidth,
            SqlAssistPreviewSettings.MaximumWidth);
        settings.Preview.Height = Clamp(
            Height,
            SqlAssistPreviewSettings.MinimumHeight,
            SqlAssistPreviewSettings.MaximumHeight);

        LoadFrom(settings); // 把收斂後的值反映回頁面，使用者才看得到實際生效的設定。
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        return Math.Max(minimum, Math.Min(maximum, value));
    }
}
