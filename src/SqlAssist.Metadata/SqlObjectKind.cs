using System;

namespace SqlAssist.Metadata;

public enum SqlObjectKind
{
    Unknown = 0,
    Table,
    View,
    Procedure,
    ScalarFunction,
    InlineTableFunction,
    TableValuedFunction,
    Synonym
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
    public static bool HasColumns(this SqlObjectKind kind)
    {
        return kind is SqlObjectKind.Table or SqlObjectKind.View;
    }

    /// <summary>是否為以 T-SQL 定義、可取得原始程式碼的模組。</summary>
    public static bool IsModule(this SqlObjectKind kind)
    {
        return kind is SqlObjectKind.Procedure
            or SqlObjectKind.ScalarFunction
            or SqlObjectKind.InlineTableFunction
            or SqlObjectKind.TableValuedFunction
            or SqlObjectKind.View;
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
            _ => "Object"
        };
    }
}
