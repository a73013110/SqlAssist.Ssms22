namespace SqlAssist.Core.Preview;

/// <summary>預覽目前由誰打開，也就決定它活多久。</summary>
public enum PreviewMode
{
    /// <summary>沒有預覽，也沒有展開意圖。</summary>
    Hidden,

    /// <summary>建議清單上展開：跟著清單的選取換內容，清單結束就收。</summary>
    Browse,

    /// <summary>使用者指名一個名稱（Ctrl+F12、Ctrl＋點擊、提示連結）：游標離開那個名稱就收。</summary>
    Named,

    /// <summary>釘住：只有使用者自己關。</summary>
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

    /// <summary>另一份建議清單開始了。</summary>
    SessionStarted,

    /// <summary>Esc、關閉鈕。</summary>
    Dismiss
}

/// <summary>
/// 浮動預覽的去留：一個預覽狀態加一件事，答案只有收或不收。
/// </summary>
/// <remarks>
/// 規則只有一條：<b>預覽活到使用者的意圖離開它為止，焦點變化永遠不算。</b>
/// 意圖由打開的方式決定——從清單打開的是在挑項目，清單結束就沒事了；指名打開的是在看
/// 某個名稱，游標或編輯離開那個名稱就沒事了；釘住的是要一直看，只有使用者自己關。
///
/// 以前的收起散在四處（清單事件、編輯器失焦後非同步檢查鍵盤焦點、清單專屬的滑鼠追蹤、
/// 平台移除），結果看的是「那一瞬間鍵盤焦點在誰身上」：同樣切到別的程式，點過預覽與
/// 沒點過的收法不同；指名打開的預覽則完全沒有游標規則，點別行也不收。
///
/// 所以焦點與可見度根本不是訊號：失焦、切到別的程式或分頁、錨點捲出畫面都只是暫時
/// 看不見，回來時照原樣出現（由承載視窗自己藏起來再出現），不會走到這裡。
/// </remarks>
public static class PreviewLifecycle
{
    /// <summary>這個訊號會不會收掉目前的預覽。</summary>
    public static bool Closes(PreviewMode mode, PreviewSignal signal) => (mode, signal) switch
    {
        (PreviewMode.Hidden, _) => false,
        (_, PreviewSignal.Dismiss) => true,
        (PreviewMode.Browse, PreviewSignal.SessionEnded or PreviewSignal.SessionStarted) => true,
        (PreviewMode.Named, PreviewSignal.CaretLeftAnchor or PreviewSignal.AnchorEdited) => true,
        _ => false
    };

    /// <summary>這個來源打開的預覽屬於哪一種。</summary>
    public static PreviewMode ModeFor(PreviewTrigger trigger) => trigger switch
    {
        PreviewTrigger.CompletionArrow or PreviewTrigger.CompletionDelay => PreviewMode.Browse,
        _ => PreviewMode.Named
    };

    /// <summary>
    /// 這個來源能不能換掉眼前的預覽。
    /// </summary>
    /// <remarks>
    /// 一個編輯器只有一個預覽，使用者明確要求的新內容一律換掉舊的。唯一的例外是
    /// 釘住的預覽遇上「停夠久自動展開」：那不是使用者的要求，讓它換掉等於釘住只撐到
    /// 下一次在清單裡停下來。
    /// </remarks>
    public static bool CanReplace(PreviewMode current, PreviewTrigger trigger) =>
        current != PreviewMode.Pinned || trigger != PreviewTrigger.CompletionDelay;

    /// <summary>按下圖釘之後的狀態；再按一次放開時回到指名，照游標規則收。</summary>
    public static PreviewMode TogglePin(PreviewMode mode) => mode switch
    {
        PreviewMode.Hidden => PreviewMode.Hidden,
        PreviewMode.Pinned => PreviewMode.Named,
        _ => PreviewMode.Pinned
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
