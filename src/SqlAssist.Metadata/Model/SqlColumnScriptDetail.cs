namespace SqlAssist.Metadata.Model;

/// <summary>
/// 重建資料行定義才需要的細節。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlColumnInfo"/> 分開，是為了讓資料行模型的表面維持在「建議清單、
/// 滑鼠停留提示與 <c>SELECT *</c> 展開要問的那幾件事」。定序、識別值種子、
/// 預設值條件約束的名稱這些只有指令碼看得懂，攤平進去之後那個型別會有二十個
/// 屬性，而讀它的四條路徑裡有三條一個都用不到。
///
/// 型別的原始欄位（<see cref="TypeName"/> 到 <see cref="Scale"/>）刻意留著，
/// 不只留 <see cref="SqlColumnInfo.DataType"/> 那個已經格式化好的字串：
/// <c>nvarchar(200)</c> 反解析回來才能改寫成 <c>[nvarchar] (200)</c>，
/// 而反解析一個已經組好的型別字串是猜，不是讀。
/// </remarks>
public sealed class SqlColumnScriptDetail
{
    /// <summary>沒有查到細節時的空值；所有欄位都是「沒有」。</summary>
    public static readonly SqlColumnScriptDetail None = new(string.Empty, 0, 0, 0);

    public SqlColumnScriptDetail(
        string typeName,
        short maxLength,
        byte precision,
        byte scale,
        string? collationName = null,
        string? identitySeed = null,
        string? identityIncrement = null,
        string? defaultConstraintName = null,
        bool defaultIsSystemNamed = false,
        bool isPersisted = false,
        bool isSparse = false,
        bool isRowGuidCol = false)
    {
        TypeName = typeName ?? string.Empty;
        MaxLength = maxLength;
        Precision = precision;
        Scale = scale;
        CollationName = collationName;
        IdentitySeed = identitySeed;
        IdentityIncrement = identityIncrement;
        DefaultConstraintName = defaultConstraintName;
        DefaultIsSystemNamed = defaultIsSystemNamed;
        IsPersisted = isPersisted;
        IsSparse = isSparse;
        IsRowGuidCol = isRowGuidCol;
    }

    /// <summary><c>sys.types.name</c>，沒有加長度也沒有加括號。</summary>
    public string TypeName { get; }

    /// <summary><c>sys.columns.max_length</c>，以位元組計；-1 是 <c>max</c>。</summary>
    public short MaxLength { get; }

    public byte Precision { get; }

    public byte Scale { get; }

    /// <summary>資料行定序；非字元型別為 null。</summary>
    public string? CollationName { get; }

    /// <summary>
    /// <c>IDENTITY</c> 的種子與遞增量。
    /// </summary>
    /// <remarks>
    /// 存成字串而不是數值：<c>sys.identity_columns</c> 的這兩欄是 <c>sql_variant</c>，
    /// 實際型別隨資料行自己的型別而變，一個 <c>decimal(38,0)</c> 的識別資料行會讓
    /// 任何一種整數轉型當場溢位——而那會讓整份中繼資料被降級成「這一輪沒有資料」。
    /// 與 <see cref="SqlSequenceInfo"/> 的界限值同一個理由、同一種處置。
    /// </remarks>
    public string? IdentitySeed { get; }

    public string? IdentityIncrement { get; }

    /// <summary>預設值條件約束的名稱；沒有預設值時為 null。</summary>
    public string? DefaultConstraintName { get; }

    /// <summary>
    /// 名稱是引擎自己配的（<c>DF__Loan__Stat__2A4B4B5E</c> 這種）。
    /// </summary>
    /// <remarks>
    /// 那種名字每建一次就換一個，寫進指令碼會讓同一張表在兩個資料庫裡的
    /// 條件約束名稱對不起來，所以要不要寫由選項決定。
    /// </remarks>
    public bool DefaultIsSystemNamed { get; }

    /// <summary>計算資料行的值有實際存下來。</summary>
    public bool IsPersisted { get; }

    public bool IsSparse { get; }

    public bool IsRowGuidCol { get; }
}
