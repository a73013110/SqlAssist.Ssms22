using System;
using SqlAssist.Metadata.Formatting;

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
    /// 定義不在 <c>sys.sql_modules</c> 裡，而是由目錄檢視的幾個欄位組出來的。
    /// </summary>
    /// <remarks>
    /// 同義字與序列。<c>OBJECT_DEFINITION</c> 對這兩種一律回傳 NULL，
    /// 但它們的定義並沒有比模組少一分——只是存在 <c>sys.synonyms</c> 與
    /// <c>sys.sequences</c> 的欄位裡，組回 T-SQL 的那一份寫在
    /// <see cref="SqlCatalogScript"/>。
    ///
    /// 分出這一條而不是到處寫 <c>is Synonym or Sequence</c>：問這件事的地方有四個
    /// （載入定義、判斷指令碼寫不寫得出來、組指令碼、滑鼠停留提示），
    /// 而漏掉其中一個的症狀是那條路徑安靜地退回「這一類沒有指令碼」。
    /// </remarks>
    public static bool HasSynthesizedDefinition(this SqlObjectKind kind)
    {
        return kind is SqlObjectKind.Synonym or SqlObjectKind.Sequence;
    }

    /// <summary>指令碼就是定義本文，不必從欄位重建。</summary>
    /// <remarks>
    /// 模組的定義來自 <c>OBJECT_DEFINITION</c>，同義字與序列的由
    /// <see cref="SqlCatalogScript"/> 組出來，兩者到了
    /// <see cref="SqlObjectStructure.BuildScript"/> 之後走的是同一條路：
    /// 直接把 <see cref="SqlObjectDetail.Definition"/> 交出去。
    /// </remarks>
    public static bool ScriptsFromDefinition(this SqlObjectKind kind)
    {
        return kind.IsModule() || kind.HasSynthesizedDefinition();
    }

    /// <summary>
    /// <see cref="SqlObjectStructure.BuildScript"/> 寫得出可以執行的 T-SQL 嗎。
    /// </summary>
    /// <remarks>
    /// 模組、同義字與序列給定義本文，資料表重建 <c>CREATE TABLE</c>，
    /// 資料表型別重建 <c>CREATE TYPE ... AS TABLE</c>。
    /// 只剩認不出來的種類寫不出東西——那時 F12 把它整段註解掉。
    ///
    /// 這一條刻意由浮動預覽的指令碼分頁與 F12 共用。兩邊各留一份判斷的症狀
    /// 就是資料表型別那一次：F12 擋掉了，預覽卻把一個型別寫成 <c>CREATE TABLE</c>，
    /// 而那份文字文件上明說可以直接執行。
    /// </remarks>
    public static bool HasExecutableScript(this SqlObjectKind kind)
    {
        return kind.ScriptsFromDefinition() ||
            kind is SqlObjectKind.Table or SqlObjectKind.TableType;
    }

    /// <summary>是否為要寫成 <c>名稱(引數…)</c> 才呼叫得動的函式。</summary>
    /// <remarks>
    /// 三種都算，而且刻意不分純量與資料表值：引數清單的寫法一模一樣，
    /// 差別只在它出現在運算式位置還是資料來源位置，而那由上下文決定，不由種類決定。
    ///
    /// 括號在 T-SQL 裡不是選擇性的——<c>SELECT dbo.fn_DueDate</c> 不是「呼叫但沒傳
    /// 引數」，而是一個語法錯誤；沒有參數的函式也一樣要寫 <c>()</c>。
    /// 這正是提交之後要補上引數清單的理由，見 <c>SqlFunctionCallExpansion</c>。
    /// </remarks>
    public static bool IsFunction(this SqlObjectKind kind)
    {
        return kind is SqlObjectKind.ScalarFunction
            or SqlObjectKind.InlineTableFunction
            or SqlObjectKind.TableValuedFunction;
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
