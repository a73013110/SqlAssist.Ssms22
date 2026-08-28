namespace SqlAssist.Core.Settings;

/// <summary>結構預覽視窗什麼時候出現。</summary>
public enum SqlPreviewMode
{
    /// <summary>不顯示浮動預覽；建議清單改用平台內建的精簡說明提示。</summary>
    Off,

    /// <summary>選取停在同一項超過設定的毫秒數就自動展開。</summary>
    Delay,

    /// <summary>只有按下向右鍵才展開，展開後跟著選取移動，按向左鍵收合。</summary>
    RightArrow
}
