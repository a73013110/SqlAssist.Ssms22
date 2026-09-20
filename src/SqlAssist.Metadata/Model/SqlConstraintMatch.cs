namespace SqlAssist.Metadata.Model;

/// <summary>條件約束的四種寫法；<see cref="None"/> 表示父物件上找不到這個名稱。</summary>
/// <remarks>
/// 四種共用一個 <see cref="SqlObjectKind.Constraint"/>（理由見
/// <see cref="SqlObjectKinds.FromSysObjectType"/>），但寫成 T-SQL 時是四句完全
/// 不同的敘述，所以指令碼那一層仍然要分得出來。
/// </remarks>
public enum SqlConstraintForm
{
    None = 0,

    /// <summary><c>DEFAULT</c>：運算式與資料行都在父物件的資料行上。</summary>
    Default,

    /// <summary><c>CHECK</c>。</summary>
    Check,

    /// <summary>主索引鍵或唯一鍵；兩者都是父物件上的一個索引。</summary>
    Key,

    ForeignKey
}

/// <summary>
/// 在父物件的結構上找到的那一個條件約束。
/// </summary>
/// <remarks>
/// 條件約束身上沒有自己的定義，它的每一個欄位都住在父物件的第四層裡。這個型別是
/// 「在那張表上把它找出來」的<b>唯一</b>一份答案：指令碼要它決定寫成哪一句，
/// 而「這一次的資料夠不夠」（<see cref="SqlObjectStructure.CanBuildExecutableScript"/>）
/// 也要它回答「那張表上真的還有這個名字嗎」。兩處各找一次的症狀是屬性說寫得出來、
/// 組出來的卻是一段註解。
/// </remarks>
public readonly struct SqlConstraintMatch
{
    private SqlConstraintMatch(
        SqlConstraintForm form,
        SqlColumnInfo? column,
        SqlCheckConstraint? check,
        SqlIndexInfo? index,
        SqlForeignKeyInfo? foreignKey)
    {
        Form = form;
        Column = column;
        Check = check;
        Index = index;
        ForeignKey = foreignKey;
    }

    /// <summary>父物件上沒有這個名稱。</summary>
    public static SqlConstraintMatch None => default;

    public static SqlConstraintMatch ForDefault(SqlColumnInfo column) =>
        new(SqlConstraintForm.Default, column, null, null, null);

    public static SqlConstraintMatch ForCheck(SqlCheckConstraint check) =>
        new(SqlConstraintForm.Check, null, check, null, null);

    public static SqlConstraintMatch ForKey(SqlIndexInfo index) =>
        new(SqlConstraintForm.Key, null, null, index, null);

    public static SqlConstraintMatch ForForeignKey(SqlForeignKeyInfo foreignKey) =>
        new(SqlConstraintForm.ForeignKey, null, null, null, foreignKey);

    public SqlConstraintForm Form { get; }

    /// <summary>掛著這個 <c>DEFAULT</c> 的資料行；其餘三種為 null。</summary>
    public SqlColumnInfo? Column { get; }

    public SqlCheckConstraint? Check { get; }

    /// <summary>主索引鍵或唯一鍵背後的那個索引；其餘三種為 null。</summary>
    public SqlIndexInfo? Index { get; }

    public SqlForeignKeyInfo? ForeignKey { get; }

    public bool Found => Form != SqlConstraintForm.None;
}
