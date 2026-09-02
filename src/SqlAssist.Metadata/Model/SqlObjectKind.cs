using System;

namespace SqlAssist.Metadata.Model;

public enum SqlObjectKind
{
    Unknown = 0,
    Table,
    View,
    Procedure,
    ScalarFunction,
    InlineTableFunction,
    TableValuedFunction,
    Synonym,
    Trigger,
    Sequence,

    /// <summary>使用者自訂資料表型別；<c>DECLARE @t dbo.XType</c> 的那個型別。</summary>
    TableType
}

public static class SqlObjectKinds
{
    /// <summary>把 sys.objects.type 對應到列舉；未知型別回傳 <see cref="SqlObjectKind.Unknown"/>。</summary>
    public static SqlObjectKind FromSysObjectType(string? type)
    {
        return (type ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "U" => SqlObjectKind.Table,
            "V" => SqlObjectKind.View,
            "P" or "PC" => SqlObjectKind.Procedure,
            "FN" or "FS" => SqlObjectKind.ScalarFunction,
            "IF" => SqlObjectKind.InlineTableFunction,
            "TF" or "FT" => SqlObjectKind.TableValuedFunction,
            "SN" => SqlObjectKind.Synonym,
            "TR" or "TA" => SqlObjectKind.Trigger,
            "SO" => SqlObjectKind.Sequence,

            // TT 不是 sys.objects 的型別代碼，是查詢把 sys.table_types
            // UNION 進來時自己貼的標籤，與同義字的 SN 同一個做法。
            "TT" => SqlObjectKind.TableType,
            _ => SqlObjectKind.Unknown
        };
    }

    /// <summary>是否為可以出現在 FROM／JOIN 後方的資料來源。</summary>
    public static bool IsDataSource(this SqlObjectKind kind)
    {
        return kind is SqlObjectKind.Table
            or SqlObjectKind.View
            or SqlObjectKind.InlineTableFunction
            or SqlObjectKind.TableValuedFunction
            or SqlObjectKind.Synonym;
    }

    /// <summary>是否具有欄位集合。</summary>
    /// <remarks>
    /// 資料表型別算在內：它的欄位在 <c>sys.columns</c> 裡查得到，
    /// 查詢用的正是 <c>sys.table_types.type_table_object_id</c>。
    /// </remarks>
    public static bool HasColumns(this SqlObjectKind kind)
    {
        return kind is SqlObjectKind.Table or SqlObjectKind.View or SqlObjectKind.TableType;
    }

    /// <summary>是否為以 T-SQL 定義、可取得原始程式碼的模組。</summary>
    public static bool IsModule(this SqlObjectKind kind)
    {
        return kind is SqlObjectKind.Procedure
            or SqlObjectKind.ScalarFunction
            or SqlObjectKind.InlineTableFunction
            or SqlObjectKind.TableValuedFunction
            or SqlObjectKind.Trigger
            or SqlObjectKind.View;
    }

    /// <summary>
    /// <see cref="SqlObjectStructure.BuildScript"/> 寫得出可以執行的 T-SQL 嗎。
    /// </summary>
    /// <remarks>
    /// 模組給定義原文，資料表重建 <c>CREATE TABLE</c>，資料表型別重建
    /// <c>CREATE TYPE ... AS TABLE</c>。其餘（同義字、序列、認不出來的種類）
    /// 只給得出一段給人看的摘要，那不是 T-SQL——F12 因此把它整段註解掉。
    ///
    /// 這一條刻意由浮動預覽的指令碼分頁與 F12 共用。兩邊各留一份判斷的症狀
    /// 就是資料表型別那一次：F12 擋掉了，預覽卻把一個型別寫成 <c>CREATE TABLE</c>，
    /// 而那份文字文件上明說可以直接執行。
    /// </remarks>
    public static bool HasExecutableScript(this SqlObjectKind kind)
    {
        return kind.IsModule() || kind is SqlObjectKind.Table or SqlObjectKind.TableType;
    }

    /// <summary>是否為 <c>EXEC</c> 呼叫得動、因而有具名參數的模組。</summary>
    /// <remarks>
    /// 純量函式算在內：<c>EXEC @fee = dbo.fn_Fee 1</c> 是合法的寫法，
    /// 而它的參數同樣有名字。
    /// </remarks>
    public static bool IsExecutable(this SqlObjectKind kind)
    {
        return kind is SqlObjectKind.Procedure or SqlObjectKind.ScalarFunction;
    }

    public static string ToDisplayName(this SqlObjectKind kind)
    {
        return kind switch
        {
            SqlObjectKind.Table => "Table",
            SqlObjectKind.View => "View",
            SqlObjectKind.Procedure => "Procedure",
            SqlObjectKind.ScalarFunction => "Scalar function",
            SqlObjectKind.InlineTableFunction => "Inline table function",
            SqlObjectKind.TableValuedFunction => "Table-valued function",
            SqlObjectKind.Synonym => "Synonym",
            SqlObjectKind.Trigger => "Trigger",
            SqlObjectKind.Sequence => "Sequence",
            SqlObjectKind.TableType => "Table type",
            _ => "Object"
        };
    }
}
