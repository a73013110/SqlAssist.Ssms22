using SqlAssist.Metadata.Dictionary;

namespace SqlAssist.Metadata.Tests.Dictionary;

/// <summary>
/// 測試用的圖書館資料字典：三張表一個檢視，<c>ReaderNo</c> 在三個地方、型別與說明各有出入。
/// </summary>
internal static class LibraryDictionary
{
    public static SqlDataDictionary Create() =>
        new("Library", new[]
        {
            new SqlDictionaryObject(1, "dbo", "Lib_Reader", SqlDictionaryObjectKind.Table, "讀者", new[]
            {
                new SqlDictionaryColumn(1, "ReaderNo", "varchar(10)", isNullable: false, isPrimaryKey: true, description: "讀者編號"),
                new SqlDictionaryColumn(2, "ReaderName", "nvarchar(50)", isNullable: false, description: "讀者姓名"),
            }),
            new SqlDictionaryObject(2, "dbo", "Loan", SqlDictionaryObjectKind.Table, null, new[]
            {
                new SqlDictionaryColumn(1, "LoanNo", "int", isNullable: false, isPrimaryKey: true, isIdentity: true),
                new SqlDictionaryColumn(2, "ReaderNo", "varchar(10)", isNullable: false),
                new SqlDictionaryColumn(3, "LoanDate", "date", isNullable: false, description: "借閱日期"),
            }),
            new SqlDictionaryObject(3, "dbo", "LoanDetail", SqlDictionaryObjectKind.Table, "借閱明細", new[]
            {
                new SqlDictionaryColumn(1, "LoanNo", "int", isNullable: false),
                new SqlDictionaryColumn(2, "CopyNo", "varchar(20)", isNullable: false, description: "館藏編號"),
            }),
            new SqlDictionaryObject(4, "rpt", "LoanByReader", SqlDictionaryObjectKind.View, null, new[]
            {
                new SqlDictionaryColumn(1, "READERNO", "varchar(12)", isNullable: true),
                new SqlDictionaryColumn(2, "LoanCount", "int", isNullable: true),
            }),
        });

    /// <summary>不看編輯，只看載入當時的說明。</summary>
    public static string Loaded(SqlDescriptionTarget target, string original) => original;
}
