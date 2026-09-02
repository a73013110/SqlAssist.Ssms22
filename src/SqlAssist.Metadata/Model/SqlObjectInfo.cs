using System;
using SqlAssist.Metadata.Formatting;

namespace SqlAssist.Metadata.Model;

/// <summary>
/// 資料庫物件的輕量描述。第一層載入只取這些欄位，不包含欄位清單與定義本文，
/// 因此即使資料庫有數千個物件也能快速取回並常駐快取。
/// </summary>
public sealed class SqlObjectInfo
{
    /// <param name="schemaName">
    /// 結構描述名稱；指令碼自己宣告的暫存資料表與資料表變數沒有結構描述，
    /// 那時傳空字串。
    /// </param>
    /// <remarks>
    /// 空的結構描述刻意不擋掉，也刻意不用 <c>dbo</c> 頂替：
    /// <c>[dbo].[#tmp]</c> 是假的，而 <c>[dbo].[@rows]</c> 連文法都不成立，
    /// 兩者出現在紀錄檔裡只會讓人去追一個不存在的物件。
    /// </remarks>
    public SqlObjectInfo(int objectId, string schemaName, string name, SqlObjectKind kind)
    {
        if (schemaName is null)
        {
            throw new ArgumentNullException(nameof(schemaName));
        }

        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("物件名稱不可為空。", nameof(name));
        }

        ObjectId = objectId;
        SchemaName = schemaName;
        Name = name;
        Kind = kind;
    }

    public int ObjectId { get; }

    public string SchemaName { get; }

    public string Name { get; }

    public SqlObjectKind Kind { get; }

    /// <summary>加上方括號的完整名稱，例如 <c>[dbo].[Lib_Reader]</c>。</summary>
    /// <remarks>
    /// 沒有結構描述時只寫名稱本身，而且照 <see cref="SqlIdentifier.QuoteIfNeeded"/>
    /// 的規則——那正是暫存資料表與資料表變數，包上方括號的寫法不是假的就是不合法。
    /// </remarks>
    public string QualifiedName =>
        SchemaName.Length == 0
            ? SqlIdentifier.QuoteIfNeeded(Name)
            : $"{SqlIdentifier.Quote(SchemaName)}.{SqlIdentifier.Quote(Name)}";

    public override string ToString() => QualifiedName;
}
