namespace SqlAssist.Core;

/// <summary>
/// SqlAssist 的全部設定，一次讀進來的一份不可變快照。
/// </summary>
/// <remarks>
/// 每一個屬性對應 <c>SqlAssist.registration.json</c> 裡的一個 moniker，
/// 屬性的預設值必須與該檔案的 <c>default</c> 一致——讀不到 Unified Settings
/// 時（服務缺席、尚未註冊、值型別不符）就是靠這裡的預設值繼續運作。
///
/// 刻意設計成不可變：設定的來源只有一個（Unified Settings），
/// 更新時整份換掉即可。呼叫端拿到的永遠是一致的一組值，
/// 不必為了避免彼此覆寫而複製快照。
/// </remarks>
public sealed class SqlAssistSettings
{
    /// <summary>sqlAssist.general.enabled</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// sqlAssist.general.uppercaseKeywordsOnType
    /// </summary>
    /// <remarks>
    /// 只影響「打完關鍵字、輸入分隔字元時把它改寫成大寫」。
    /// 建議清單裡要不要列出關鍵字與這個值無關，關鍵字一律會列出來。
    /// </remarks>
    public bool UppercaseKeywordsOnType { get; init; } = true;

    /// <summary>sqlAssist.suggestions.enabled</summary>
    public bool SuggestionsEnabled { get; init; } = true;

    /// <summary>sqlAssist.suggestions.triggerAfterCharacters</summary>
    public int TriggerAfterCharacters { get; init; } = SqlAssistLimits.DefaultTriggerCharacters;

    /// <summary>sqlAssist.suggestions.includeSnippets：ssf、ap、af 這三個程式碼片段。</summary>
    public bool IncludeSnippets { get; init; } = true;

    /// <summary>
    /// sqlAssist.suggestions.includeDatabaseObjects
    /// </summary>
    /// <remarks>
    /// 整個中繼資料層的閘門：物件清單、欄位建議、敘述範圍欄位與欄位預熱
    /// 全都掛在它下面。關掉之後不會對連線的資料庫送出任何查詢。
    /// </remarks>
    public bool IncludeDatabaseObjects { get; init; } = true;

    /// <summary>sqlAssist.suggestions.qualifyObjectNames</summary>
    public bool QualifyObjectNames { get; init; } = true;

    /// <summary>sqlAssist.suggestions.useSquareBrackets</summary>
    public bool UseSquareBrackets { get; init; }

    /// <summary>sqlAssist.structure.hoverEnabled：滑鼠停留提示，與浮動預覽是兩個獨立的表面。</summary>
    public bool HoverEnabled { get; init; } = true;

    /// <summary>sqlAssist.structure.previewMode</summary>
    public SqlPreviewMode PreviewMode { get; init; } = SqlPreviewMode.Delay;

    /// <summary>
    /// sqlAssist.structure.previewDelay
    /// </summary>
    /// <remarks>
    /// 只用於 <see cref="SqlPreviewMode.Delay"/>：選取停在同一項多久才展開。
    /// 展開後換選取時的查詢緩衝是實作細節，不由這個值決定。
    /// </remarks>
    public int PreviewDelayMilliseconds { get; init; } = SqlAssistLimits.DefaultPreviewDelay;

    /// <summary>sqlAssist.structure.previewPlacement</summary>
    public SqlPreviewPlacement PreviewPlacement { get; init; } = SqlPreviewPlacement.Stacked;

    /// <summary>
    /// sqlAssist.structure.previewFontSize
    /// </summary>
    /// <remarks>
    /// 只影響資料格、分頁與標題這些自己排版的部分，其餘字級由這個值推導：
    /// 標題大一號，摘要與欄位標題小一號，徽章再小一點。
    /// 指令碼分頁跟的是編輯器的字型與字級，刻意不受這個值影響——那一份文字
    /// 是要拿去跟查詢視窗裡的程式碼對照的。
    /// </remarks>
    public double PreviewFontSize { get; init; } = SqlAssistLimits.DefaultPreviewFontSize;

    /// <summary>sqlAssist.diagnostics.verboseLogging</summary>
    public bool VerboseLogging { get; init; }
}
