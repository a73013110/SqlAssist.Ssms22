using SqlAssist.Core.Parsing;

namespace SqlAssist.Metadata.Model;

/// <summary>游標位置所指的資料庫物件，以及（若游標停在欄位上）該欄位。</summary>
public sealed class SqlObjectLocation
{
    public SqlObjectLocation(
        SqlIdentifierReference reference,
        SqlObjectInfo objectInfo,
        SqlColumnInfo? column = null,
        SqlObjectDetail? detail = null)
    {
        Reference = reference;
        Object = objectInfo;
        Column = column;
        Detail = detail;
    }

    public SqlIdentifierReference Reference { get; }

    /// <summary>物件本身；游標停在欄位上時，是該欄位所屬的物件。</summary>
    public SqlObjectInfo Object { get; }

    /// <summary>游標停在欄位上時的欄位描述，否則為 null。</summary>
    public SqlColumnInfo? Column { get; }

    /// <summary>
    /// 已經讀好的明細；要向中繼資料要的物件為 null。
    /// </summary>
    /// <remarks>
    /// 只有指令碼自己宣告的暫存資料表、資料表變數與 CTE 會帶著它——它們的
    /// <c>object_id</c> 一律是 0，而中繼資料的第二、三層快取就是照編號存的。
    /// 呼叫端拿到這一份就不要再問中繼資料：問過去不是白跑一次查詢，
    /// 就是拿到另一個同樣沒有編號的東西。
    /// </remarks>
    public SqlObjectDetail? Detail { get; }
}
