namespace SqlAssist.Metadata.Model;

/// <summary>
/// 物件的本文用什麼寫成；決定「取不到 T-SQL 定義」該怎麼解釋。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlObjectKind"/> 是兩個軸：種類說它怎麼用（<c>EXEC</c>、<c>FROM</c>），
/// 這一個說它的本文在哪裡。<c>sp_executesql</c> 與 <c>sp_help</c> 都是預存程序，
/// 前者卻根本沒有 T-SQL 本文——混成同一種的症狀是預覽把它說成加密或沒有權限，
/// 使用者去查一個不存在的原因。
/// </remarks>
public enum SqlObjectImplementation
{
    /// <summary>T-SQL 本文，存在 <c>sys.sql_modules</c>；資料表這類沒有本文的也歸這裡。</summary>
    TransactSql,

    /// <summary>CLR 組件實作的程序、函式與觸發程序：本文編譯在組件裡。</summary>
    Clr,

    /// <summary>擴充預存程序：SQL Server 以原生程式實作，連參數都不在目錄檢視裡。</summary>
    Extended
}

public static class SqlObjectImplementations
{
    /// <summary>把 <c>sys.objects.type</c> 對應到實作方式；認不得的一律當成 T-SQL。</summary>
    public static SqlObjectImplementation FromSysObjectType(string? type)
    {
        return SqlObjectKinds.NormalizeType(type) switch
        {
            "PC" or "FS" or "FT" or "TA" => SqlObjectImplementation.Clr,
            "X" => SqlObjectImplementation.Extended,
            _ => SqlObjectImplementation.TransactSql
        };
    }

    /// <summary>本文是不是 T-SQL；不是時定義一律取不到，而那不是加密或權限的問題。</summary>
    public static bool HasTransactSqlBody(this SqlObjectImplementation implementation) =>
        implementation == SqlObjectImplementation.TransactSql;
}
