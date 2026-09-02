using System.Text;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Formatting;

/// <summary>
/// 同義字與序列的 <c>CREATE</c> 指令碼。
/// </summary>
/// <remarks>
/// 這兩種物件的定義<b>不在</b> <c>sys.sql_modules</c> 裡，<c>OBJECT_DEFINITION</c>
/// 對它們一律回傳 NULL——它們的定義就是目錄檢視上的那幾個欄位。所以這一份不是
/// 「重建一個近似值」，而是把定義本身寫回 T-SQL 的樣子，與模組拿到定義原文是
/// 同一件事，只是來源不同。
///
/// 組出來的文字放進 <see cref="SqlObjectDetail.Definition"/>，滑鼠停留提示、
/// 浮動預覽的指令碼分頁與 F12 三條路徑因此共用同一份文字。各自照著自己的資料
/// 再組一次的症狀，就是同一個同義字在三個地方寫法不同——而其中總有一份會忘記
/// 更新。
/// </remarks>
public static class SqlCatalogScript
{
    /// <summary>
    /// <c>CREATE SYNONYM … FOR …;</c>。
    /// </summary>
    /// <param name="baseObjectName">
    /// <c>sys.synonyms.base_object_name</c>；那一欄存的已經是加好方括號的多段式名稱
    /// （<c>[伺服器].[資料庫].[結構描述].[物件]</c>，用得到幾段就有幾段），
    /// 因此原樣寫回去，不要再自己拆一次——拆錯的症狀是一個跨伺服器的同義字
    /// 被寫成本機的名稱，而那份指令碼照樣執行得動。
    /// </param>
    /// <returns>取不到指向的物件時回傳 null，由呼叫端當成「這一輪沒有定義」。</returns>
    public static string? ForSynonym(SqlObjectInfo objectInfo, string? baseObjectName)
    {
        if (objectInfo is null || string.IsNullOrWhiteSpace(baseObjectName))
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append("CREATE SYNONYM ").AppendLine(objectInfo.QualifiedName);
        builder.Append("FOR ").Append(baseObjectName!.Trim()).AppendLine(";");
        return builder.ToString();
    }

    /// <summary>
    /// <c>CREATE SEQUENCE … AS 型別 START WITH … ;</c>。
    /// </summary>
    /// <remarks>
    /// 每一個子句都寫出來，連引擎預設就是那個值的也不省略：這份文字的用途是
    /// 「照著執行就得到同一個序列」，而 <c>MINVALUE</c>／<c>MAXVALUE</c> 的預設值
    /// 是隨型別變的，省略等於把它交給執行的那台伺服器決定。
    ///
    /// 目前值（<c>current_value</c>）刻意不寫進去。它每取一次號就變，寫進一份
    /// 給人看的定義裡只會讓兩次打開的內容不一樣；而 <c>START WITH</c> 收的本來
    /// 就是建立時的起始值。
    /// </remarks>
    /// <returns>查不到那一列時回傳 null。</returns>
    public static string? ForSequence(SqlObjectInfo objectInfo, SqlSequenceInfo? sequence)
    {
        if (objectInfo is null || sequence is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append("CREATE SEQUENCE ").AppendLine(objectInfo.QualifiedName);
        builder.Append("    AS ").AppendLine(sequence.DataType);
        builder.Append("    START WITH ").AppendLine(sequence.StartValue);
        builder.Append("    INCREMENT BY ").AppendLine(sequence.Increment);
        builder.Append("    MINVALUE ").AppendLine(sequence.MinimumValue);
        builder.Append("    MAXVALUE ").AppendLine(sequence.MaximumValue);
        builder.AppendLine(sequence.IsCycling ? "    CYCLE" : "    NO CYCLE");
        builder.Append("    ").Append(CacheClause(sequence)).AppendLine(";");
        return builder.ToString();
    }

    /// <remarks>
    /// 三種寫法而不是兩種：關掉快取是 <c>NO CACHE</c>，開著而且指定了大小是
    /// <c>CACHE n</c>，開著但大小交給引擎決定則是不帶數字的 <c>CACHE</c>——
    /// 最後那一種在 <c>sys.sequences</c> 裡是 <c>is_cached = 1</c> 加上
    /// <c>cache_size</c> 為 NULL，寫成 <c>CACHE 0</c> 會被拒絕。
    /// </remarks>
    private static string CacheClause(SqlSequenceInfo sequence)
    {
        if (!sequence.IsCached)
        {
            return "NO CACHE";
        }

        return sequence.CacheSize is { } size ? $"CACHE {size}" : "CACHE";
    }
}
