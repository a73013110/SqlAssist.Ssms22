using System.Collections.Generic;
using SqlAssist.Core.Completion;

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
    private static readonly (string Name, string Description, bool TakesArguments)[] Definitions =
    {
        // 精確數值
        ("BIGINT", "整數（8 位元組）", false),
        ("INT", "整數（4 位元組）", false),
        ("SMALLINT", "整數（2 位元組）", false),
        ("TINYINT", "整數（0 到 255）", false),
        ("BIT", "0、1 或 NULL", false),
        ("DECIMAL", "固定有效位數與小數位數", true),
        ("NUMERIC", "同 DECIMAL", true),
        ("MONEY", "貨幣（8 位元組）", false),
        ("SMALLMONEY", "貨幣（4 位元組）", false),

        // 概略數值
        ("FLOAT", "浮點數", false),
        ("REAL", "浮點數（等同 FLOAT(24)）", false),

        // 日期與時間
        ("DATE", "日期", false),
        ("TIME", "時間", false),
        ("DATETIME2", "日期與時間（建議用它取代 DATETIME）", false),
        ("DATETIMEOFFSET", "日期、時間與時區位移", false),
        ("DATETIME", "日期與時間（精確度 3.33 毫秒）", false),
        ("SMALLDATETIME", "日期與時間（精確度 1 分鐘）", false),

        // 字元
        ("CHAR", "固定長度非 Unicode 字串", true),
        ("VARCHAR", "可變長度非 Unicode 字串", true),
        ("NCHAR", "固定長度 Unicode 字串", true),
        ("NVARCHAR", "可變長度 Unicode 字串", true),
        ("TEXT", "已淘汰，改用 VARCHAR(MAX)", false),
        ("NTEXT", "已淘汰，改用 NVARCHAR(MAX)", false),

        // 二進位
        ("BINARY", "固定長度二進位", true),
        ("VARBINARY", "可變長度二進位", true),
        ("IMAGE", "已淘汰，改用 VARBINARY(MAX)", false),

        // 其他
        ("UNIQUEIDENTIFIER", "GUID（16 位元組）", false),
        ("XML", "XML 文件或片段", false),
        ("SQL_VARIANT", "可放多種型別的值", false),
        ("HIERARCHYID", "階層位置", false),
        ("GEOMETRY", "平面空間資料", false),
        ("GEOGRAPHY", "地理空間資料", false),
        ("ROWVERSION", "資料列版本（自動遞增）", false),
        ("TIMESTAMP", "已淘汰，改用 ROWVERSION", false),
        ("SYSNAME", "系統物件名稱（等同 NVARCHAR(128)）", false),
        ("TABLE", "資料表變數或資料表值參數", false),
        ("CURSOR", "資料指標變數", false)
    };

    private static IReadOnlyList<SqlSuggestion>? _suggestions;

    private static readonly object Gate = new();

    /// <summary>內建型別的建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> All
    {
        get
        {
            lock (Gate)
            {
                return _suggestions ??= Build();
            }
        }
    }

    private static IReadOnlyList<SqlSuggestion> Build()
    {
        var suggestions = new List<SqlSuggestion>(Definitions.Length);

        foreach (var (name, description, takesArguments) in Definitions)
        {
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
