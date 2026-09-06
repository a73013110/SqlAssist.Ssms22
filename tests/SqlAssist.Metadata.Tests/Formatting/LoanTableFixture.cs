using System.Collections.Generic;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Tests.Formatting;

/// <summary>
/// 一張把還原項目都用上的資料表，供三組風格的快照與健檢規則共用。
/// </summary>
/// <remarks>
/// 刻意只有一張：三組風格各自準備一份測試資料的話，其中一份少了 <c>COLLATE</c>
/// 或少了篩選索引，那個選項就會在那一組風格底下永遠沒被驗過，而快照仍然是綠的。
///
/// 這張表同時是健檢規則的來源：INCLUDE 帶了一個 <c>nvarchar(max)</c>、
/// <c>LoanUser</c> 與 <c>CreateUser</c> 同語意卻一個 <c>varchar</c> 一個
/// <c>nvarchar</c>、<c>Status</c> 是列舉語意卻沒有 CHECK、六個 <c>datetime</c>。
/// 四條規則各自準備一張表的話，規則之間會互相看不見。
/// </remarks>
internal static class LoanTableFixture
{
    private const string Collation = "Chinese_Taiwan_Stroke_CI_AS";

    public static SqlObjectStructure Create() =>
        new(
            new SqlObjectDetail(new SqlObjectInfo(1, "dbo", "Loan", SqlObjectKind.Table), Columns()),
            Indexes());

    private static IReadOnlyList<SqlColumnInfo> Columns() => new[]
    {
        Identity(1, "LoanId", "int", 4, 10),
        Column(2, "PublicId", "uniqueidentifier", 16, nullable: false,
            defaultDefinition: "(newsequentialid())", defaultName: "DF_Loan_PublicId"),
        Column(3, "Status", "tinyint", 1, nullable: false, precision: 3),
        Text(4, "Title", "nvarchar", 400),
        Text(5, "Remark", "nvarchar", -1),
        Text(6, "BranchNo", "varchar", 50),
        Text(7, "LoanUser", "varchar", 50),
        Column(8, "LoanTime", "datetime", 8, nullable: false, precision: 23, scale: 3),
        Column(9, "DueTime", "datetime", 8, nullable: true, precision: 23, scale: 3),
        Text(10, "TargetBranchNo", "varchar", 50),
        Column(11, "RenewCount", "int", 4, nullable: false, precision: 10,
            defaultDefinition: "((0))", defaultName: "DF_Loan_RenewCount"),
        Column(12, "IsActive", "bit", 1, nullable: false, precision: 1,
            defaultDefinition: "((1))", defaultName: "DF_Loan_IsActive"),
        Text(13, "CreateUser", "nvarchar", 100),
        Column(14, "CreateTime", "datetime", 8, nullable: false, precision: 23, scale: 3,
            defaultDefinition: "(getdate())", defaultName: "DF_Loan_CreateTime"),
        Text(15, "UpdateUser", "nvarchar", 100, nullable: true),
        Column(16, "UpdateTime", "datetime", 8, nullable: true, precision: 23, scale: 3)
    };

    private static IReadOnlyList<SqlIndexInfo> Indexes() => new[]
    {
        new SqlIndexInfo(
            1,
            "PK_Loan",
            new[] { new SqlIndexColumn("LoanId") },
            isPrimaryKey: true,
            isUnique: true,
            typeDescription: "CLUSTERED"),
        new SqlIndexInfo(
            2,
            "IX_Loan_1",
            new[]
            {
                new SqlIndexColumn("Status"),
                new SqlIndexColumn("LoanTime", isDescending: true),
                new SqlIndexColumn("IsActive", isIncluded: true),
                new SqlIndexColumn("DueTime", isIncluded: true)
            },
            typeDescription: "NONCLUSTERED"),
        new SqlIndexInfo(
            3,
            "IX_Loan_2",
            new[] { new SqlIndexColumn("PublicId") },
            isUnique: true,
            typeDescription: "NONCLUSTERED"),
        new SqlIndexInfo(
            4,
            "IX_Loan_3",
            new[]
            {
                new SqlIndexColumn("TargetBranchNo"),
                new SqlIndexColumn("LoanTime"),
                new SqlIndexColumn("LoanId", isIncluded: true),
                new SqlIndexColumn("PublicId", isIncluded: true),
                new SqlIndexColumn("Status", isIncluded: true),
                new SqlIndexColumn("Title", isIncluded: true),
                new SqlIndexColumn("Remark", isIncluded: true),
                new SqlIndexColumn("BranchNo", isIncluded: true),
                new SqlIndexColumn("LoanUser", isIncluded: true),
                new SqlIndexColumn("DueTime", isIncluded: true),
                new SqlIndexColumn("RenewCount", isIncluded: true)
            },
            typeDescription: "NONCLUSTERED",
            filterDefinition: "([IsActive]=(1))")
    };

    private static SqlColumnInfo Identity(int ordinal, string name, string type, short length, byte precision) =>
        new(
            ordinal,
            name,
            SqlTypeFormatter.Format(type, length, precision, 0),
            isNullable: false,
            isIdentity: true,
            isPrimaryKey: true,
            script: new SqlColumnScriptDetail(
                type, length, precision, 0, identitySeed: "1", identityIncrement: "1"));

    /// <summary>字元型別：長度以位元組計，Unicode 的字元數是它的一半。</summary>
    private static SqlColumnInfo Text(
        int ordinal,
        string name,
        string type,
        short maxLength,
        bool nullable = false) =>
        new(
            ordinal,
            name,
            SqlTypeFormatter.Format(type, maxLength, 0, 0),
            nullable,
            script: new SqlColumnScriptDetail(type, maxLength, 0, 0, collationName: Collation));

    private static SqlColumnInfo Column(
        int ordinal,
        string name,
        string type,
        short maxLength,
        bool nullable,
        byte precision = 0,
        byte scale = 0,
        string? defaultDefinition = null,
        string? defaultName = null) =>
        new(
            ordinal,
            name,
            SqlTypeFormatter.Format(type, maxLength, precision, scale),
            nullable,
            defaultDefinition: defaultDefinition,
            script: new SqlColumnScriptDetail(
                type, maxLength, precision, scale, defaultConstraintName: defaultName));
}
