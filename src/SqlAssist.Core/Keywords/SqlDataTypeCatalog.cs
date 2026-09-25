using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// T-SQL 的內建資料型別。
/// </summary>
/// <remarks>
/// 與內建函式、全域變數同一個理由只能手寫：型別名稱在文法上不是關鍵字，
/// <c>INT</c>、<c>NVARCHAR</c> 在 ScriptDom 眼中只是識別字，token 列舉裡沒有它們。
/// 關鍵字目錄的 191 個字裡因此一個型別都沒有。
///
/// 已淘汰的 <c>TEXT</c>、<c>NTEXT</c>、<c>IMAGE</c>、<c>TIMESTAMP</c> <b>收</b>，
/// 只是在說明欄寫明替代品：它們今天仍然運作，而維護舊結構描述的人本來就要打出它們。
/// 這與全域變數排除 <c>@@REMSERVER</c> 不衝突——那個變數回報的功能整個被拿掉了，
/// 打出來也得不到有意義的值。標準是「還有用就收，只是標清楚」。
/// </remarks>
public static class SqlDataTypeCatalog
{
    /// <summary>
    /// 名稱、說明，以及提交時要不要接著左括號。
    /// </summary>
    /// <remarks>
    /// 只有「幾乎一定會寫長度或有效位數」的型別帶左括號，與內建函式同一個道理：
    /// 少按一次鍵，而游標剛好停在引數上。<c>DATETIME2</c>、<c>FLOAT</c> 不帶——
    /// 那兩個用預設值的寫法遠比指定的常見，補上去反而要多按一次刪除。
    /// </remarks>
    private static readonly (string Name, Func<string> Description, bool TakesArguments)[] Definitions =
    {
        // 精確數值
        ("BIGINT", () => DataTypeText.Bigint, false),
        ("INT", () => DataTypeText.Int, false),
        ("SMALLINT", () => DataTypeText.Smallint, false),
        ("TINYINT", () => DataTypeText.Tinyint, false),
        ("BIT", () => DataTypeText.Bit, false),
        ("DECIMAL", () => DataTypeText.Decimal, true),
        ("NUMERIC", () => DataTypeText.Numeric, true),
        ("MONEY", () => DataTypeText.Money, false),
        ("SMALLMONEY", () => DataTypeText.Smallmoney, false),

        // 概略數值
        ("FLOAT", () => DataTypeText.Float, false),
        ("REAL", () => DataTypeText.Real, false),

        // 日期與時間
        ("DATE", () => DataTypeText.Date, false),
        ("TIME", () => DataTypeText.Time, false),
        ("DATETIME2", () => DataTypeText.Datetime2, false),
        ("DATETIMEOFFSET", () => DataTypeText.Datetimeoffset, false),
        ("DATETIME", () => DataTypeText.Datetime, false),
        ("SMALLDATETIME", () => DataTypeText.Smalldatetime, false),

        // 字元
        ("CHAR", () => DataTypeText.Char, true),
        ("VARCHAR", () => DataTypeText.Varchar, true),
        ("NCHAR", () => DataTypeText.Nchar, true),
        ("NVARCHAR", () => DataTypeText.Nvarchar, true),
        ("TEXT", () => DataTypeText.Text, false),
        ("NTEXT", () => DataTypeText.Ntext, false),

        // 二進位
        ("BINARY", () => DataTypeText.Binary, true),
        ("VARBINARY", () => DataTypeText.Varbinary, true),
        ("IMAGE", () => DataTypeText.Image, false),

        // 其他
        ("UNIQUEIDENTIFIER", () => DataTypeText.Uniqueidentifier, false),
        ("XML", () => DataTypeText.Xml, false),
        ("SQL_VARIANT", () => DataTypeText.SqlVariant, false),
        ("HIERARCHYID", () => DataTypeText.Hierarchyid, false),
        ("GEOMETRY", () => DataTypeText.Geometry, false),
        ("GEOGRAPHY", () => DataTypeText.Geography, false),
        ("ROWVERSION", () => DataTypeText.Rowversion, false),
        ("TIMESTAMP", () => DataTypeText.Timestamp, false),
        ("SYSNAME", () => DataTypeText.Sysname, false),
        ("TABLE", () => DataTypeText.Table, false),
        ("CURSOR", () => DataTypeText.Cursor, false)
    };

    private static readonly SqlLanguageCache<IReadOnlyList<SqlSuggestion>> SuggestionCache =
        new(_ => Build());

    /// <summary>查出一個內建型別的一行說明；大小寫不敏感。</summary>
    /// <remarks>
    /// 這一行是型別說明的唯一出處，滑鼠停留提示與建議清單問的是同一份
    /// （<see cref="SqlBuiltInDocCatalog"/>）。線性掃過的理由同
    /// <see cref="SqlFunctionCatalog.TryGetSignature"/>。
    /// </remarks>
    public static bool TryGetDescription(string? name, out string description)
    {
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var (candidate, value, _) in Definitions)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                {
                    description = value();
                    return true;
                }
            }
        }

        description = string.Empty;
        return false;
    }

    /// <summary>內建型別的建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> All => SuggestionCache.Current;

    private static IReadOnlyList<SqlSuggestion> Build()
    {
        var suggestions = new List<SqlSuggestion>(Definitions.Length);

        foreach (var (name, describe, takesArguments) in Definitions)
        {
            var description = describe();

            suggestions.Add(new SqlSuggestion(
                name,
                takesArguments ? name + "(" : name,
                description,
                description,
                SuggestionKind.DataType));
        }

        return suggestions;
    }
}
