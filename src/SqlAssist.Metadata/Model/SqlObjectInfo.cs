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
    /// <param name="databaseName">
    /// 這個物件所屬的資料庫；查詢視窗自己那條連線的物件為 null。
    /// </param>
    /// <param name="serverName">
    /// 這個物件所在的連結伺服器；目前這台伺服器上的物件為 null。
    /// </param>
    public SqlObjectInfo(
        int objectId,
        string schemaName,
        string name,
        SqlObjectKind kind,
        string? databaseName = null,
        string? serverName = null)
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
        DatabaseName = databaseName;
        ServerName = serverName;
    }

    /// <summary>
    /// <c>object_id</c>。
    /// </summary>
    /// <remarks>
    /// 只在<b>它自己那個資料庫裡</b>唯一。跨資料庫時兩個不同的物件拿到同一個
    /// 編號是常態，所以任何以這個編號查快取的地方都要先換到
    /// <see cref="DatabaseName"/> 那一份目錄——不換的症狀是拿到另一個資料庫裡
    /// 剛好同號的那個物件的欄位。
    /// </remarks>
    public int ObjectId { get; }

    /// <summary>
    /// 這個物件所屬的資料庫；查詢視窗自己那條連線的物件為 null。
    /// </summary>
    /// <remarks>
    /// 帶在物件身上而不是由呼叫端一路傳下去：滑鼠停留提示、結構預覽、F12、
    /// 提交後展開都會拿著一個 <see cref="SqlObjectInfo"/> 回頭要第二、三、四層，
    /// 每一條都自己記住「這是從哪個資料庫來的」就是五份會各自忘記更新的狀態。
    /// </remarks>
    public string? DatabaseName { get; }

    /// <summary>
    /// 這個物件所在的連結伺服器；目前這台伺服器上的物件為 null。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="DatabaseName"/> 同一個理由，而且非有不可：<c>object_id</c>
    /// 連「同一台伺服器」都不保證，換到別台之後同號的物件更是毫無關係。
    /// 少了這一欄，跨伺服器物件的欄位、F12 與結構預覽會拿本機同號的東西回答。
    /// </remarks>
    public string? ServerName { get; }

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
