using System;

namespace SqlAssist.Metadata.Model;

/// <summary>
/// 一個掛在別人身上的物件（條件約束、觸發程序）在目錄上的落點：父物件是誰、
/// 它自己是哪一種，以及 <c>DEFAULT</c> 掛在哪一個資料行上。
/// </summary>
/// <remarks>
/// 只帶目錄上的事實，不帶任何「畫在樹的哪一格」的知識：後者住在
/// <see cref="SqlObjectExplorerUrn"/>，而同一份事實也餵得了指令碼那條路徑。
///
/// <see cref="ChildType"/> 是<b>子物件自己</b>的 <c>sys.objects.type</c>，不是父物件的。
/// 四種條件約束共用一個 <see cref="SqlObjectKind.Constraint"/>，
/// 而它們在物件總管上分屬「索引鍵」與「條件約束」兩個資料夾、三種節點，
/// 只看種類的話指不出是哪一個。
/// </remarks>
public sealed class SqlObjectParent
{
    /// <param name="childType">子物件自己的 <c>sys.objects.type</c>（<c>D</c>、<c>C</c>、<c>PK</c>、<c>UQ</c>、<c>F</c>、<c>TR</c>…）。</param>
    /// <param name="columnName"><c>DEFAULT</c> 掛的資料行；其餘種類為 null。</param>
    public SqlObjectParent(SqlObjectInfo objectInfo, string childType, string? columnName)
    {
        Object = objectInfo ?? throw new ArgumentNullException(nameof(objectInfo));
        ChildType = childType ?? "";
        ColumnName = columnName;
    }

    /// <summary>父物件；帶著資料庫與伺服器，下游才換得到正確的那一份目錄。</summary>
    public SqlObjectInfo Object { get; }

    /// <summary>子物件自己的型別代碼；認不得時是空字串。</summary>
    public string ChildType { get; }

    /// <summary><c>DEFAULT</c> 掛的資料行名稱；其餘種類為 null。</summary>
    public string? ColumnName { get; }

    public override string ToString() =>
        ColumnName is null ? $"{Object.QualifiedName}:{ChildType}" : $"{Object.QualifiedName}.{ColumnName}:{ChildType}";
}
