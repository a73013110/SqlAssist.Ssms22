using System;

namespace SqlAssist.Core.Scripting;

/// <summary>
/// 產生指令碼時的所有開關。
/// </summary>
/// <remarks>
/// 這是純資料，放在 Core 而不是 Metadata：命令列工具與測試都要在完全沒有連線的
/// 情況下組出一份選項，而 Metadata 那一層開始就需要 <c>System.Data</c>。
///
/// 寫成 <c>record</c> 是為了 <c>with</c>：三組風格是三個具名的預設值，使用者的
/// 自訂一律表達成「某個風格 <c>with</c> 幾項覆寫」，而不是另一份手抄的完整清單。
/// 手抄的那一份會在新增選項時忘記跟上，症狀是新選項對自訂過的人沒有預設值。
///
/// 屬性依「這個開關管什麼」分組。與 T-SQL 排版有關的那幾組對將來的 Markdown 或
/// Mermaid 輸出沒有意義，由那些 <c>renderer</c> 自行忽略——現在就拆成兩層設定，
/// 換來的是每一個呼叫端都要多帶一個參數，而第二個 renderer 還不存在。
/// </remarks>
public sealed record SqlScriptOptions
{
    /// <summary>Fidelity 的「Script as CREATE」。</summary>
    public static readonly SqlScriptOptions Fidelity = new();

    /// <summary>SSMS 內建的「編寫指令碼為 &gt; CREATE 至」。</summary>
    public static readonly SqlScriptOptions SsmsNative = new()
    {
        Style = SqlScriptStyle.SsmsNative,
        Indent = "\t",
        BracePlacement = SqlBracePlacement.SameLine,
        SpaceBeforeTypeArguments = false,
        SpaceAfterArgumentComma = false,
        NullabilityOrder = SqlNullabilityOrder.AfterIdentity,
        PrimaryKeyPlacement = SqlConstraintPlacement.Inline,
        UniqueConstraintPlacement = SqlConstraintPlacement.Inline,
        OmitAscendingKeyword = false,
        SetOptions = SqlSetOptionOutput.FromCatalog,
        Collation = SqlCollationOutput.WhenDifferentFromDatabase,
        IncludeNonDefaultIndexOptions = false
    };

    /// <summary>只留執行得起來所必需的部分。</summary>
    /// <remarks>
    /// 系統配的條件約束名稱刻意省略，這份輸出因此<b>不能</b>拿來逐字比對重新產生的結果：
    /// 沒有名稱的條件約束在目的地會被引擎重配一個。要往返一致就用另外兩組。
    /// </remarks>
    public static readonly SqlScriptOptions Minimal = new()
    {
        Style = SqlScriptStyle.Minimal,
        QuoteIdentifiers = false,
        QuoteDataTypes = false,
        SpaceBeforeTypeArguments = false,
        SpaceAfterArgumentComma = false,
        ConstraintNaming = SqlConstraintNaming.OnlyUserNamed,
        PrimaryKeyPlacement = SqlConstraintPlacement.Inline,
        UniqueConstraintPlacement = SqlConstraintPlacement.Inline,
        Collation = SqlCollationOutput.Never,
        BatchSeparation = SqlBatchSeparation.None,
        SetOptions = SqlSetOptionOutput.None,
        IncludeFilegroup = false,
        IncludeTextImageOn = false,
        IncludeExtendedProperties = false,
        IncludeNonDefaultIndexOptions = false,
        StatementTerminator = ";"
    };

    /// <summary>這份選項是從哪一組風格衍生的；只影響設定的顯示與存放。</summary>
    public SqlScriptStyle Style { get; init; } = SqlScriptStyle.Fidelity;

    // ── 版面 ──────────────────────────────────────────────────────────

    /// <summary>資料行定義前面的縮排。Fidelity 頂格，因此預設是空字串。</summary>
    public string Indent { get; init; } = string.Empty;

    public SqlBracePlacement BracePlacement { get; init; } = SqlBracePlacement.NewLine;

    /// <summary>敘述結尾的分號；空字串代表不寫。</summary>
    /// <remarks>
    /// 分號不是可有可無的排版：Fidelity 與 SSMS 都不寫，而 <c>MERGE</c> 之類的敘述
    /// 少了它會語法錯誤。這裡管的只有本擴充自己組出來的那些敘述。
    /// </remarks>
    public string StatementTerminator { get; init; } = string.Empty;

    // ── 識別字與型別 ──────────────────────────────────────────────────

    /// <summary>識別字加不加方括號。</summary>
    /// <remarks>
    /// 關掉之後仍然會替保留字與形狀不合法的名稱加括號——那不是風格，
    /// 少了它指令碼執行不了。判斷只有 <c>Core/Parsing/SqlIdentifier</c> 一份。
    /// </remarks>
    public bool QuoteIdentifiers { get; init; } = true;

    /// <summary>型別名稱加不加方括號（<c>[int]</c> 對 <c>int</c>）。</summary>
    public bool QuoteDataTypes { get; init; } = true;

    /// <summary>型別名稱與長度括號之間留不留空格（<c>[nvarchar] (200)</c>）。</summary>
    public bool SpaceBeforeTypeArguments { get; init; } = true;

    /// <summary>括號內的逗號後面留不留空格（<c>IDENTITY(1, 1)</c>）。</summary>
    public bool SpaceAfterArgumentComma { get; init; } = true;

    // ── 資料行 ────────────────────────────────────────────────────────

    public SqlCollationOutput Collation { get; init; } = SqlCollationOutput.Always;

    public SqlNullabilityOrder NullabilityOrder { get; init; } = SqlNullabilityOrder.BeforeIdentity;

    /// <summary>寫出 <c>IDENTITY(seed, increment)</c> 而不只是 <c>IDENTITY</c>。</summary>
    public bool IncludeIdentitySeed { get; init; } = true;

    /// <summary>計算資料行寫出 <c>PERSISTED</c>。</summary>
    public bool IncludePersisted { get; init; } = true;

    public bool IncludeSparse { get; init; } = true;

    public bool IncludeRowGuidCol { get; init; } = true;

    // ── 條件約束 ──────────────────────────────────────────────────────

    public SqlConstraintNaming ConstraintNaming { get; init; } = SqlConstraintNaming.Always;

    public SqlConstraintPlacement PrimaryKeyPlacement { get; init; } =
        SqlConstraintPlacement.SeparateStatement;

    public SqlConstraintPlacement UniqueConstraintPlacement { get; init; } =
        SqlConstraintPlacement.SeparateStatement;

    public bool IncludeCheckConstraints { get; init; } = true;

    public bool IncludeForeignKeys { get; init; } = true;

    /// <summary>外來鍵前面加 <c>WITH NOCHECK</c>，把既有資料排除在驗證之外。</summary>
    public bool ForeignKeysWithNoCheck { get; init; }

    // ── 索引 ──────────────────────────────────────────────────────────

    public bool IncludeIndexes { get; init; } = true;

    /// <summary>索引鍵的遞增資料行省略 <c>ASC</c>；<c>DESC</c> 一律寫出來。</summary>
    public bool OmitAscendingKeyword { get; init; } = true;

    /// <summary>
    /// 索引選項只寫出與預設值不同的那幾個。
    /// </summary>
    /// <remarks>
    /// 全部寫出來（SSMS 的做法）會讓每一個索引後面掛八個 <c>= OFF</c>，
    /// 而那些字沒有一個帶資訊；全部不寫則會漏掉真的調過的 <c>FILLFACTOR</c>
    /// 與 <c>DATA_COMPRESSION</c>，那是靜靜地改掉一張表的行為。
    /// </remarks>
    public bool IncludeNonDefaultIndexOptions { get; init; } = true;

    // ── 儲存位置 ──────────────────────────────────────────────────────

    public bool IncludeFilegroup { get; init; } = true;

    /// <summary>有 LOB 資料行時寫出 <c>TEXTIMAGE_ON</c>。</summary>
    public bool IncludeTextImageOn { get; init; } = true;

    public bool IncludePartitionScheme { get; init; } = true;

    // ── 附加物件 ──────────────────────────────────────────────────────

    public bool IncludeExtendedProperties { get; init; } = true;

    public SqlExtendedPropertyProcedure ExtendedPropertyProcedure { get; init; } =
        SqlExtendedPropertyProcedure.Add;

    public bool IncludeTriggers { get; init; }

    // ── 批次與樣板 ────────────────────────────────────────────────────

    public SqlBatchSeparation BatchSeparation { get; init; } = SqlBatchSeparation.BetweenStatements;

    public string BatchSeparator { get; init; } = "GO";

    public SqlSetOptionOutput SetOptions { get; init; } = SqlSetOptionOutput.None;

    public SqlExistenceCheck ExistenceCheck { get; init; } = SqlExistenceCheck.None;

    /// <summary>模組（程序、函式、觸發程序、檢視）的定義寫成 CREATE 還是 ALTER。</summary>
    public SqlModuleStatement ModuleStatement { get; init; } = SqlModuleStatement.Create;

    /// <summary>檔頭註解：來源伺服器、資料庫、產生時間、工具版本與選項摘要。</summary>
    public bool IncludeHeaderComment { get; init; }

    /// <summary>把結構健檢的發現寫成指令碼裡的註解。</summary>
    public bool IncludeAnalyzerComments { get; init; }

    /// <summary>取得某一組內建風格。</summary>
    public static SqlScriptOptions ForStyle(SqlScriptStyle style)
    {
        switch (style)
        {
            case SqlScriptStyle.Fidelity:
                return Fidelity;
            case SqlScriptStyle.SsmsNative:
                return SsmsNative;
            case SqlScriptStyle.Minimal:
                return Minimal;
            default:
                throw new ArgumentOutOfRangeException(nameof(style), style, "未知的指令碼風格。");
        }
    }
}
