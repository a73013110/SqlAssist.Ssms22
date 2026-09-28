namespace SqlAssist.Core.Preview;

/// <summary>畫面上這一份內容由誰打開，也就決定它活多久。</summary>
public enum PreviewMode
{
    /// <summary>沒有預覽，也沒有展開意圖。</summary>
    Hidden,

    /// <summary>建議清單上展開：跟著清單的選取換內容，清單結束就收（或還給釘住的那一份）。</summary>
    Browse,

    /// <summary>使用者指名一個名稱（Ctrl+F12、Ctrl＋點擊、提示連結）：游標離開那個名稱就收。</summary>
    Named,

    /// <summary>釘住的那一份：只有使用者自己關。</summary>
    Pinned
}

/// <summary>打開或換掉預覽內容的來源。</summary>
public enum PreviewTrigger
{
    /// <summary>建議清單開著時按向右鍵。</summary>
    CompletionArrow,

    /// <summary>建議清單上停在同一項夠久。</summary>
    CompletionDelay,

    /// <summary>Ctrl+F12、Ctrl＋點擊或工具選單。</summary>
    Command,

    /// <summary>滑鼠停留提示裡的「開啟完整結構／說明」。</summary>
    HoverLink
}

/// <summary>可能讓預覽收起來的事。</summary>
public enum PreviewSignal
{
    /// <summary>驅動預覽的建議清單結束（挑選完成或關閉）。</summary>
    SessionEnded,

    /// <summary>游標移到錨點那個名稱以外的地方。</summary>
    CaretLeftAnchor,

    /// <summary>錨點那段文字被改了。</summary>
    AnchorEdited,

    /// <summary>捲動讓錨點離開了畫面；切到別的分頁不算，那是整個編輯器看不見。</summary>
    AnchorScrolledOut,

    /// <summary>另一份建議清單開始了。</summary>
    SessionStarted,

    /// <summary>清單上展開時按向左鍵收合。</summary>
    Collapse,

    /// <summary>Esc、關閉鈕。</summary>
    Dismiss
}

/// <summary>一個訊號對眼前預覽的結果。</summary>
public enum PreviewOutcome
{
    Keep,

    Close,

    /// <summary>清單借用結束，釘住的窗換回釘住的那一份。</summary>
    ReturnToPin
}

/// <summary>
/// 浮動預覽的去留：一個預覽狀態加一件事，答案只有留、收或還給釘住的那一份。
/// </summary>
/// <remarks>
/// 規則只有一條：<b>預覽活到使用者的意圖離開它為止，焦點變化永遠不算。</b>
/// 意圖由打開的方式決定——從清單打開的是在挑項目，清單結束就沒事了；指名打開的是在看
/// 某個名稱，游標或編輯離開那個名稱就沒事了；釘住的是要一直看，只有使用者自己關。
///
/// <b>釘住的是位置，不是擋住別的預覽。</b>釘住之後那扇窗就是這個編輯器預覽出現的地方：
/// 指名打開的換掉釘住的那一份、仍然釘著；清單上展開的暫時借用這扇窗，清單結束就還回去。
/// 以前釘住只擋「停夠久自動展開」，向右鍵與 Ctrl+F12 則默默解除釘住、跳回錨點——在清單上
/// 看起來是預覽壞了，按了 Ctrl+F12 又看起來是圖釘沒用。
///
/// 焦點與整個編輯器的可見度根本不是訊號：失焦、切到別的程式或分頁都只是暫時看不見，
/// 回來時照原樣出現（由承載視窗自己藏起來再出現），不會走到這裡。錨點捲出畫面則是：捲走是
/// 使用者自己把視線移開那個名稱，跟游標離開同一回事。清單上展開的不收，清單自己也跟著錨點
/// 藏起來；釘住的不錨在名稱上，本來就不受影響。
/// </remarks>
public static class PreviewLifecycle
{
    /// <summary>這個訊號對目前的預覽做什麼。</summary>
    /// <param name="pinned">視窗釘在某處；清單上展開時代表正借用釘住的窗。</param>
    public static PreviewOutcome Resolve(PreviewMode mode, bool pinned, PreviewSignal signal) => (mode, signal) switch
    {
        (PreviewMode.Hidden, _) => PreviewOutcome.Keep,
        (_, PreviewSignal.Dismiss) => PreviewOutcome.Close,
        (PreviewMode.Browse, PreviewSignal.SessionEnded or PreviewSignal.SessionStarted or PreviewSignal.Collapse) =>
            pinned ? PreviewOutcome.ReturnToPin : PreviewOutcome.Close,
        (PreviewMode.Named, PreviewSignal.CaretLeftAnchor or PreviewSignal.AnchorEdited or PreviewSignal.AnchorScrolledOut) =>
            PreviewOutcome.Close,
        _ => PreviewOutcome.Keep
    };

    /// <summary>這個來源打開的內容屬於哪一種；釘住時指名的直接成為釘住的那一份。</summary>
    public static PreviewMode ModeFor(PreviewTrigger trigger, bool pinned) => trigger switch
    {
        PreviewTrigger.CompletionArrow or PreviewTrigger.CompletionDelay => PreviewMode.Browse,
        _ => pinned ? PreviewMode.Pinned : PreviewMode.Named
    };

    /// <summary>
    /// 按下圖釘之後的狀態。
    /// </summary>
    /// <remarks>
    /// 釘住眼前這一份；放開時回到指名，照游標規則收。清單正借用釘住的窗時放開，窗回到清單旁邊
    /// 繼續跟著選取，釘住的那一份跟著放掉——圖釘是亮的，按下去的意思就是「不要釘著了」。
    /// </remarks>
    public static (PreviewMode Mode, bool Pinned) TogglePin(PreviewMode mode, bool pinned) => mode switch
    {
        PreviewMode.Hidden => (PreviewMode.Hidden, false),
        PreviewMode.Pinned => (PreviewMode.Named, false),
        PreviewMode.Browse when pinned => (PreviewMode.Browse, false),
        _ => (PreviewMode.Pinned, true)
    };

    /// <summary>
    /// 游標還算不算在錨點上；名稱前後緣都算在內。
    /// </summary>
    /// <remarks>
    /// 游標停在名稱最後一個字元之後，是使用者剛點進名稱尾端或剛打完它的位置，
    /// 那仍然是在看這個名稱。
    /// </remarks>
    public static bool IsOnAnchor(int anchorStart, int anchorEnd, int caret) =>
        caret >= anchorStart && caret <= anchorEnd;
}
