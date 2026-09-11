using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Wildcards;

/// <summary>游標後方那個可以展開成欄位清單的萬用字元。</summary>
public sealed class SqlWildcardTarget
{
    public SqlWildcardTarget(
        int start,
        int length,
        string? qualifierText,
        bool qualify,
        IReadOnlyList<SqlColumnSource> sources,
        string? databaseSwitch = null)
    {
        Start = start;
        Length = length;
        QualifierText = qualifierText;
        Qualify = qualify;
        Sources = sources;
        DatabaseSwitch = databaseSwitch;
    }

    /// <summary>要被替換掉的範圍起點，含使用者寫下的限定字（<c>a.*</c> 從 <c>a</c> 開始）。</summary>
    public int Start { get; }

    public int Length { get; }

    /// <summary>
    /// 使用者寫在 <c>*</c> 前面的限定字原文，沒寫時為 null。
    /// </summary>
    /// <remarks>
    /// 保留原文而不是解析後的名稱：<c>dbo.PUBLISHER.*</c> 要展開成
    /// <c>dbo.PUBLISHER.欄位</c>，把它改寫成 <c>PUBLISHER.欄位</c> 雖然也合法，
    /// 卻是使用者沒有要求的改動。
    /// </remarks>
    public string? QualifierText { get; }

    /// <summary>
    /// 展開的欄位要不要加上限定字。
    /// </summary>
    /// <remarks>
    /// 兩種情形要加：使用者自己寫了限定字（<c>a.*</c>），
    /// 或敘述裡有兩個以上的資料來源——那時不加限定字的欄位名稱可能模稜兩可，
    /// 展開出來的 SQL 直接執行不了。
    /// </remarks>
    public bool Qualify { get; }

    /// <summary>欄位的來源，順序就是展開後的欄位順序。</summary>
    public IReadOnlyList<SqlColumnSource> Sources { get; }

    /// <summary>
    /// 這份指令碼最後一個 <c>USE</c> 指名的資料庫；沒有 <c>USE</c> 時為 null。
    /// </summary>
    /// <remarks>
    /// 展開寫回去的是欄位名稱，拿錯資料庫的同名資料表就是把別人的欄位貼進
    /// 使用者的指令碼，而畫面上看不出來。理由與用法見
    /// <see cref="SqlDatabaseSwitch"/>。
    /// </remarks>
    public string? DatabaseSwitch { get; }
}
