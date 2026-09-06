namespace SqlAssist.Core.Scripting;

/// <summary>三組內建的輸出風格。</summary>
/// <remarks>
/// 風格不是「主題」，是一整組選項的具名預設值：使用者選了風格之後仍然可以逐項覆寫，
/// 覆寫的結果由 <see cref="SqlScriptOptionsSerializer"/> 存成使用者設定。
/// 值本身會寫進設定檔，改名等於讓所有存過設定的人回到預設。
/// </remarks>
public enum SqlScriptStyle
{
    /// <summary>還原度優先：條件約束獨立成敘述、每一段接 GO、型別與長度之間留空格。</summary>
    Fidelity,

    /// <summary>與 SSMS「編寫指令碼為」一致：SET 選項開頭、主索引鍵內嵌、明確寫出 ASC。</summary>
    SsmsNative,

    /// <summary>只留執行得起來所必需的部分：不加方括號、不寫填色群組、不寫系統配的名稱。</summary>
    Minimal
}

/// <summary>資料行的 <c>COLLATE</c> 什麼時候寫出來。</summary>
public enum SqlCollationOutput
{
    Never,

    /// <summary>只有與資料庫定序不同時才寫；相同時省略，讓指令碼跟著目的地資料庫走。</summary>
    WhenDifferentFromDatabase,

    Always
}

/// <summary>條件約束寫在 <c>CREATE TABLE</c> 的括號裡，還是獨立成一個敘述。</summary>
public enum SqlConstraintPlacement
{
    Inline,

    SeparateStatement
}

/// <summary>條件約束的名稱要不要寫出來。</summary>
/// <remarks>
/// <c>OnlyUserNamed</c> 靠 <c>is_system_named</c> 判斷。省略系統配的名稱可以讓同一份
/// 指令碼套到多個資料庫而不撞名，代價是重新產生時名稱會換一個——所以逐字比對的
/// 快照只對 <see cref="Always"/> 成立。
/// </remarks>
public enum SqlConstraintNaming
{
    Never,

    OnlyUserNamed,

    Always
}

/// <summary><c>IDENTITY</c> 與 <c>NULL</c>／<c>NOT NULL</c> 的先後。</summary>
/// <remarks>
/// 兩種寫法都合法，差別只在還原度：Fidelity 寫
/// <c>[int] NOT NULL IDENTITY(1, 1)</c>，SSMS 寫 <c>[int] IDENTITY(1,1) NOT NULL</c>。
/// </remarks>
public enum SqlNullabilityOrder
{
    /// <summary><c>NOT NULL IDENTITY(1, 1)</c>。</summary>
    BeforeIdentity,

    /// <summary><c>IDENTITY(1,1) NOT NULL</c>。</summary>
    AfterIdentity
}

/// <summary>批次分隔字元的用法。</summary>
public enum SqlBatchSeparation
{
    None,

    /// <summary>每一個敘述後面各接一個 <c>GO</c>。</summary>
    BetweenStatements
}

/// <summary>資料行清單的開括號放在物件名稱那一行，還是自己一行。</summary>
public enum SqlBracePlacement
{
    SameLine,

    NewLine
}

/// <summary>開頭的 <c>SET ANSI_NULLS</c>／<c>SET QUOTED_IDENTIFIER</c> 從哪裡來。</summary>
public enum SqlSetOptionOutput
{
    None,

    /// <summary>固定寫 <c>ON</c>。</summary>
    AlwaysOn,

    /// <summary>依 <c>sys.tables.uses_ansi_nulls</c> 反推建立當時的值。</summary>
    FromCatalog
}

/// <summary>擴充屬性用哪一個系統程序寫入。</summary>
public enum SqlExtendedPropertyProcedure
{
    /// <summary>一律 <c>sp_addextendedproperty</c>；屬性已存在時會失敗。</summary>
    Add,

    /// <summary>先以 <c>fn_listextendedproperty</c> 判斷存不存在，再決定 add 還是 update。</summary>
    AddOrUpdate
}

/// <summary>要不要包一層存在性判斷。</summary>
public enum SqlExistenceCheck
{
    None,

    /// <summary>資料表包 <c>IF OBJECT_ID(...) IS NULL</c>，其餘物件包對應的 <c>IF NOT EXISTS</c>。</summary>
    IfNotExists
}
