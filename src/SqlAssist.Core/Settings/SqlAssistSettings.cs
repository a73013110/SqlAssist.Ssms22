namespace SqlAssist.Core.Settings;

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

    /// <summary>
    /// sqlAssist.general.expandWildcardOnTab
    /// </summary>
    /// <remarks>
    /// 游標停在選取清單的 <c>*</c> 後方時，按 Tab 把它換成完整的欄位清單，
    /// 同時決定那個「按 Tab 展開」的提示要不要出現——提示與行為是同一件事，
    /// 分成兩個開關只會讓人調出「看得到提示、按了沒反應」的組合。
    /// </remarks>
    public bool ExpandWildcardOnTab { get; init; } = true;

    /// <summary>
    /// sqlAssist.general.wildcardLayout
    /// </summary>
    /// <remarks>
    /// 只在 <see cref="ExpandWildcardOnTab"/> 開著時看得到效果；註冊檔也是這樣
    /// 用 <c>enableWhen</c> 綁住的，兩個設定因此必須留在同一個分類裡。
    /// </remarks>
    public SqlWildcardLayout WildcardLayout { get; init; } = SqlWildcardLayout.OneLineWhenShort;

    /// <summary>sqlAssist.suggestions.enabled</summary>
    public bool SuggestionsEnabled { get; init; } = true;

    /// <summary>
    /// sqlAssist.suggestions.suppressNativeMemberList
    /// </summary>
    /// <remarks>
    /// 唯一一個作用在<b>擴充之外</b>的設定：它改的是 SSMS 舊版語言服務的
    /// <c>LANGPREFERENCES2.fAutoListMembers</c>，讓那份清單不再隨打字自動彈出，
    /// 而錯誤波浪線、大綱與參數提示照舊。因此它必須被推出去，不能只放著等人來讀——
    /// 推的那一半在 <c>Ssms22/Settings/NativeMemberList</c>。
    /// </remarks>
    public bool SuppressNativeMemberList { get; init; } = true;

    /// <summary>sqlAssist.suggestions.triggerAfterCharacters</summary>
    public int TriggerAfterCharacters { get; init; } = SqlAssistLimits.DefaultTriggerCharacters;

    /// <summary>sqlAssist.suggestions.includeSnippets：內建與使用者自訂的程式碼片段。</summary>
    public bool IncludeSnippets { get; init; } = true;

    /// <summary>
    /// sqlAssist.suggestions.includeDatabaseObjects
    /// </summary>
    /// <remarks>
    /// 整個中繼資料層的閘門：物件清單、欄位建議、敘述範圍欄位與欄位預熱
    /// 全都掛在它下面。關掉之後不會對連線的資料庫送出任何查詢。
    /// </remarks>
    public bool IncludeDatabaseObjects { get; init; } = true;

    /// <summary>
    /// sqlAssist.suggestions.showCategoryFilters
    /// </summary>
    /// <remarks>
    /// 建議清單上方那排分類篩選鈕（欄位、資料表、檢視…）。
    /// 清單裡只有一種分類時本來就不會出現，這個開關管的是「有兩種以上時要不要顯示」。
    /// </remarks>
    public bool ShowCategoryFilters { get; init; } = true;

    /// <summary>sqlAssist.suggestions.qualifyObjectNames</summary>
    public bool QualifyObjectNames { get; init; } = true;

    /// <summary>sqlAssist.suggestions.useSquareBrackets</summary>
    public bool UseSquareBrackets { get; init; }

    /// <summary>
    /// sqlAssist.suggestions.expandInsertStatement
    /// </summary>
    /// <remarks>
    /// 在 <c>INSERT INTO </c> 之後提交一張資料表時，把整句展開成欄位清單加
    /// <c>VALUES</c> 預留值，而不是只補上名稱。關掉之後那個位置就跟其他位置一樣
    /// 只插入名稱——<c>INSERT INTO t SELECT …</c> 這種寫法用得多的人會想關掉它。
    /// </remarks>
    public bool ExpandInsertStatement { get; init; } = true;

    /// <summary>
    /// sqlAssist.suggestions.expandMergeStatement
    /// </summary>
    /// <remarks>
    /// 在 <c>MERGE INTO </c> 之後提交一張資料表時，把整句展開成比對鍵、
    /// <c>UPDATE SET</c>、<c>INSERT</c> 與 <c>VALUES</c>。與
    /// <see cref="ExpandInsertStatement"/> 分成兩個開關的理由與 <c>EXEC</c> 相同：
    /// 展開的東西不同，想關掉其中一個的理由也不同。
    /// </remarks>
    public bool ExpandMergeStatement { get; init; } = true;

    /// <summary>
    /// sqlAssist.suggestions.expandProcedureCall
    /// </summary>
    /// <remarks>
    /// 在 <c>EXEC </c> 之後提交一個模組時，把整句展開成具名傳值的呼叫。
    /// 與 <see cref="ExpandInsertStatement"/> 分成兩個開關而不是一個：
    /// 兩者展開的東西不同，想關掉其中一個的理由也不同。
    /// </remarks>
    public bool ExpandProcedureCall { get; init; } = true;

    /// <summary>
    /// sqlAssist.suggestions.expandFunctionCall
    /// </summary>
    /// <remarks>
    /// 提交一個使用者自訂函式時補上引數清單：<c>SELECT dbo.fn_DueDate(NULL)</c>、
    /// <c>FROM dbo.fn_LoansByReader(0)</c>。與上面三個分成獨立的開關，理由相同——
    /// 展開的東西不同，想關掉它的理由也不同：這一個補的是括號與預留值，
    /// 而括號在 T-SQL 裡本來就非寫不可。
    ///
    /// 只管使用者自訂函式。T-SQL 內建函式的左括號寫在建議項自己的插入文字裡
    /// （<c>SqlFunctionCatalog</c>），那一份不查資料庫，也不受這個開關影響。
    /// </remarks>
    public bool ExpandFunctionCall { get; init; } = true;

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
