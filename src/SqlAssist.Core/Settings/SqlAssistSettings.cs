using SqlAssist.Core.Scripting;

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
    public bool BlockMatchingEnabled { get; init; } = true;
    public bool BlockKeywordHighlight { get; init; } = true;
    public string BlockKeywordForeground { get; init; } = string.Empty;
    public string BlockKeywordBackground { get; init; } = string.Empty;
    public string BlockSymbolForeground { get; init; } = string.Empty;
    public string BlockSymbolBackground { get; init; } = string.Empty;
    public bool BlockRangeBackground { get; init; } = true;
    public bool BlockRangeInside { get; init; } = true;
    public string BlockAccentColor { get; init; } = string.Empty;
    public bool BlockStructure { get; init; } = true;
    public bool BlockOutlining { get; init; }
    public bool BlockGlyphs { get; init; } = true;
    public bool BlockOverview { get; init; } = true;
    public bool BlockContextHint { get; init; } = true;
    public bool BlockSameLineBackground { get; init; } = true;
    public bool BlockMatchParentheses { get; init; } = true;
    public bool BlockMatchCase { get; init; } = true;
    public int BlockDebounceMilliseconds { get; init; } = SqlAssistLimits.DefaultBlockDebounce;

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
    /// sqlAssist.general.autoPairDelimiters
    /// </summary>
    /// <remarks>
    /// 輸入 <c>(</c>、<c>'</c>、<c>[</c>、<c>"</c> 時補上另一半，打結尾字元時跳過補上的那一個，
    /// Backspace 刪掉開頭字元時把空的另一半一起收掉，有選取範圍時包夾它，
    /// 提交自己帶著左括號的建議（<c>GETDATE(</c>）時把右括號一起寫進去。
    /// 這幾種行為是同一件事的幾個方向，分成多個開關只會調出自相矛盾的組合
    /// （補得出來卻收不掉）。
    ///
    /// <c>BEGIN</c>…<c>END</c> 不在這個開關底下：那是程式碼片段的守備範圍，
    /// 與「每一次按鍵都要判斷」的分隔字元不是同一個機制。
    /// </remarks>
    public bool AutoPairDelimiters { get; init; } = true;

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

    /// <summary>
    /// sqlAssist.suggestions.showCategoryFilters
    /// </summary>
    /// <remarks>
    /// 建議清單上方那排分類篩選鈕（欄位、資料表、檢視…）。
    /// 清單裡只有一種分類時本來就不會出現，這個開關管的是「有兩種以上時要不要顯示」。
    /// </remarks>
    public bool ShowCategoryFilters { get; init; } = true;

    /// <summary>sqlAssist.suggestions.includeSnippets：內建與使用者自訂的程式碼片段。</summary>
    public bool IncludeSnippets { get; init; } = true;

    /// <summary>
    /// sqlAssist.suggestions.includeDatabaseObjects
    /// </summary>
    /// <remarks>
    /// 整個中繼資料層的閘門：物件清單、欄位建議、敘述範圍欄位與欄位預熱
    /// 全都掛在它下面。關掉之後不會對連線的資料庫送出任何查詢，
    /// 「插入與展開」那幾項也就沒有材料可以展開。
    /// </remarks>
    public bool IncludeDatabaseObjects { get; init; } = true;

    /// <summary>sqlAssist.insertion.qualifyObjectNames</summary>
    public bool QualifyObjectNames { get; init; } = true;

    /// <summary>sqlAssist.insertion.useSquareBrackets</summary>
    public bool UseSquareBrackets { get; init; }

    /// <summary>
    /// sqlAssist.insertion.expandWildcardOnTab
    /// </summary>
    /// <remarks>
    /// 游標停在選取清單的 <c>*</c> 後方時，按 Tab 把它換成完整的欄位清單，
    /// 同時決定那個「按 Tab 展開」的提示要不要出現——提示與行為是同一件事，
    /// 分成兩個開關只會讓人調出「看得到提示、按了沒反應」的組合。
    ///
    /// 由 Tab 觸發而不是由建議提交觸發，但使用者感覺到的是「編輯器裡多出一串欄位」，
    /// 所以歸在 <c>insertion</c> 而不是 <c>general</c>。
    /// </remarks>
    public bool ExpandWildcardOnTab { get; init; } = true;

    /// <summary>
    /// sqlAssist.insertion.wildcardLayout
    /// </summary>
    /// <remarks>
    /// 只在 <see cref="ExpandWildcardOnTab"/> 開著時看得到效果；註冊檔也是這樣
    /// 用 <c>enableWhen</c> 綁住的，兩個設定因此必須留在同一個分類裡。
    /// </remarks>
    public SqlWildcardLayout WildcardLayout { get; init; } = SqlWildcardLayout.OneLineWhenShort;

    /// <summary>
    /// sqlAssist.insertion.expandAlterDefinition
    /// </summary>
    /// <remarks>
    /// 在 <c>ALTER PROCEDURE</c>／<c>FUNCTION</c>／<c>VIEW</c>／<c>TRIGGER</c> 之後
    /// 提交一個模組時，把伺服器上的 <c>CREATE</c> 定義取回來改寫成 <c>ALTER</c> 整句貼上。
    ///
    /// 關掉它同時省下那一次查詢：<c>SqlAlterStatementExpansion.KnownDetail</c> 是
    /// <c>null</c>（定義只有中繼資料層拿得到），不建立展開就不會去問。
    /// </remarks>
    public bool ExpandAlterDefinition { get; init; } = true;

    /// <summary>
    /// sqlAssist.insertion.expandInsertStatement
    /// </summary>
    /// <remarks>
    /// 在 <c>INSERT INTO </c> 之後提交一張資料表時，把整句展開成欄位清單加
    /// <c>VALUES</c> 預留值，而不是只補上名稱。關掉之後那個位置就跟其他位置一樣
    /// 只插入名稱——<c>INSERT INTO t SELECT …</c> 這種寫法用得多的人會想關掉它。
    /// </remarks>
    public bool ExpandInsertStatement { get; init; } = true;

    /// <summary>
    /// sqlAssist.insertion.expandMergeStatement
    /// </summary>
    /// <remarks>
    /// 在 <c>MERGE INTO </c> 之後提交一張資料表時，把整句展開成比對鍵、
    /// <c>UPDATE SET</c>、<c>INSERT</c> 與 <c>VALUES</c>。與
    /// <see cref="ExpandInsertStatement"/> 分成兩個開關的理由與 <c>EXEC</c> 相同：
    /// 展開的東西不同，想關掉其中一個的理由也不同。
    /// </remarks>
    public bool ExpandMergeStatement { get; init; } = true;

    /// <summary>
    /// sqlAssist.insertion.expandProcedureCall
    /// </summary>
    /// <remarks>
    /// 在 <c>EXEC </c> 之後提交一個模組時，把整句展開成具名傳值的呼叫。
    /// 與 <see cref="ExpandInsertStatement"/> 分成兩個開關而不是一個：
    /// 兩者展開的東西不同，想關掉其中一個的理由也不同。
    /// </remarks>
    public bool ExpandProcedureCall { get; init; } = true;

    /// <summary>
    /// sqlAssist.insertion.includeOptionalParameters
    /// </summary>
    /// <remarks>
    /// 關掉之後 <c>EXEC</c> 的骨架只留必填參數。這不是「少展開一點」的折衷：
    /// 省略有預設值的參數本來就是合法的呼叫方式，而參數二三十個的程序展開出來
    /// 有一半是使用者接著要一行一行刪掉的。
    ///
    /// 是哪些參數有預設值由 <c>SqlModuleParameterDefaults</c> 從定義文字判斷，
    /// 而那份判斷本來就跑（展開時要標示「選擇性」），所以關掉不省查詢也不多花錢。
    /// 定義取不到時那份清單是空的，於是所有參數都算必填——寧可展開得多，
    /// 也不要因為讀不到定義就把該填的參數吞掉。
    ///
    /// 整支程序的參數都有預設值時，篩完一個不剩，那一次就退回只插入名稱：
    /// <c>EXEC dbo.uspFoo</c> 本身就是完整的呼叫，不是半成品。
    /// </remarks>
    public bool IncludeOptionalParameters { get; init; } = true;

    /// <summary>
    /// sqlAssist.insertion.expandFunctionCall
    /// </summary>
    /// <remarks>
    /// 提交一個使用者自訂函式時補上一對空括號，游標停在中間：
    /// <c>SELECT dbo.fn_DueDate(|)</c>、<c>FROM dbo.fn_LoansByReader(|)</c>。
    /// 預設開著，理由與其他四個展開不同——那四個補的是可以不要的方便，
    /// 這一個補的是<b>非寫不可</b>的語法：<c>SELECT dbo.fn_DueDate</c> 是語法錯誤，
    /// 沒有參數的函式也一樣要寫 <c>()</c>。
    ///
    /// 括號裡要不要再填預留值由 <see cref="ExpandFunctionArguments"/> 決定；
    /// 這一個關掉之後那一個就沒有意義了，所以它掛在這一個底下。
    ///
    /// 只管使用者自訂函式。T-SQL 內建函式的左括號寫在建議項自己的插入文字裡
    /// （<c>SqlFunctionCatalog</c>），那一份不查資料庫，也不受這個開關影響。
    /// </remarks>
    public bool ExpandFunctionCall { get; init; } = true;

    /// <summary>
    /// sqlAssist.insertion.expandFunctionArguments
    /// </summary>
    /// <remarks>
    /// 開著時括號裡再依參數型別填一組預留值：<c>SELECT dbo.fn_DueDate(NULL)</c>、
    /// <c>FROM dbo.fn_LoansByReader(0, NULL, N'')</c>，游標停在第一個引數上。
    ///
    /// 預設<b>關著</b>，與其他四個展開相反。那四個省下來的是使用者本來就要一列一列
    /// 打出來的東西；這一份預留值只是照型別猜的字面值，接著每一個都要換掉，
    /// 而且填進去之後游標旁邊站的是 <c>NULL</c> 而不是空括號——參數叫什麼、
    /// 現在輪到第幾個，反而要自己去別的地方看。空括號留給 SSMS 自己的
    /// 「陳述式完成 → 參數資訊」接手，那一份知道的比預留值多。
    ///
    /// 只在 <see cref="ExpandFunctionCall"/> 開著時有效：括號都不補的話，
    /// 沒有地方可以放引數。
    /// </remarks>
    public bool ExpandFunctionArguments { get; init; }

    /// <summary>sqlAssist.structure.hoverEnabled：滑鼠停留提示，與浮動預覽是兩個獨立的表面。</summary>
    public bool HoverEnabled { get; init; } = true;

    /// <summary>
    /// sqlAssist.structure.builtInHelp：內建名稱的滑鼠停留說明。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="HoverEnabled"/> 分開，因為關掉它的理由不同：物件結構要查中繼資料，
    /// 內建說明只查一份內嵌資料，不受連線與快取影響。已經熟記 T-SQL 的人只想關掉後者。
    /// </remarks>
    public bool BuiltInHelpEnabled { get; init; } = true;

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

    /// <summary>
    /// sqlAssist.structure.scriptStyle
    /// </summary>
    /// <remarks>
    /// F12 與浮動預覽的指令碼分頁共用這一組。逐項的開關<b>不</b>放進 Unified
    /// Settings：那是四十幾個旋鈕，而每一個都要動四處並過守門測試，設定頁也會
    /// 從五項變成五十項。風格是那些開關的具名組合，要再細調的人改的是
    /// SqlScriptOptions 本身。
    /// </remarks>
    public SqlScriptStyle ScriptStyle { get; init; } = SqlScriptStyle.Fidelity;

    /// <summary>sqlAssist.structure.scriptIncludeExtendedProperties</summary>
    /// <remarks>
    /// 單獨拉出來是因為它最常被關掉：一張三十個資料行的資料表，說明會佔掉
    /// 整份指令碼的一半以上，而要的人與不要的人各佔一半。
    /// </remarks>
    public bool ScriptIncludeExtendedProperties { get; init; } = true;

    /// <summary>sqlAssist.structure.scriptIncludeAnalyzerComments</summary>
    public bool ScriptIncludeAnalyzerComments { get; init; }

    /// <summary>sqlAssist.structure.scriptIncludeHeaderComment</summary>
    public bool ScriptIncludeHeaderComment { get; init; }

    /// <summary>sqlAssist.diagnostics.verboseLogging</summary>
    public bool VerboseLogging { get; init; }
}
