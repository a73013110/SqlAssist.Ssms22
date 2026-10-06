using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Parsing;

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
///
/// SQL Server 2025 才有的 <c>JSON</c>、<c>VECTOR</c> 同樣照收、不看連線的版本：這份清單本來就
/// 不查資料庫，而寫給新版的指令碼常在舊版的連線上編輯。版本寫在說明欄。
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
        ("JSON", () => DataTypeText.Json, false),
        ("VECTOR", () => DataTypeText.Vector, true),
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

    /// <summary>多字的 ANSI 同義字與它代表的型別；說明與要不要接左括號都取那個型別的。</summary>
    /// <remarks>
    /// 只收多字的：單字的 <c>INTEGER</c>、<c>DEC</c> 只是多一個寫法，型別清單多列它們只是雜訊；多字的少了它們，
    /// 第一個字（<c>NATIONAL</c>）在型別的位置就列不出來。型別之後的位置也要認得整段才知道型別寫完了，
    /// 見 <see cref="CountWords"/>。
    /// </remarks>
    private static readonly (string Name, string Type)[] Synonyms =
    {
        ("NATIONAL CHARACTER VARYING", "NVARCHAR"),
        ("NATIONAL CHAR VARYING", "NVARCHAR"),
        ("NATIONAL CHARACTER", "NCHAR"),
        ("NATIONAL CHAR", "NCHAR"),
        ("NATIONAL TEXT", "NTEXT"),
        ("CHARACTER VARYING", "VARCHAR"),
        ("CHAR VARYING", "VARCHAR"),
        ("BINARY VARYING", "VARBINARY"),
        ("DOUBLE PRECISION", "FLOAT")
    };

    private static readonly string[][] SynonymWords =
        Synonyms.Select(synonym => synonym.Name.Split(' ')).OrderByDescending(words => words.Length).ToArray();

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
        if (!string.IsNullOrEmpty(name) && Find(name!) is { } definition)
        {
            description = definition.Description();
            return true;
        }

        description = string.Empty;
        return false;
    }

    /// <summary>名稱是內建型別或同義字；大小寫不敏感。</summary>
    internal static bool IsBuiltIn(string name) => Find(name) is not null;

    /// <summary>內建型別的名稱，不含同義字；語法著色用。</summary>
    internal static IEnumerable<string> Names => Definitions.Select(definition => definition.Name);

    /// <summary>
    /// 從 <paramref name="index"/> 起是多字型別（<c>NATIONAL CHARACTER VARYING</c>、<c>DOUBLE PRECISION</c>）時的字數，
    /// 取最長的；不是就回 0。
    /// </summary>
    /// <param name="end">不含的上限。</param>
    public static int CountWords(IReadOnlyList<SqlToken> tokens, int index, int end)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        foreach (var words in SynonymWords)
        {
            if (index + words.Length > end)
            {
                continue;
            }

            var matched = true;

            for (var offset = 0; offset < words.Length && matched; offset++)
            {
                var token = tokens[index + offset];
                matched = token.Kind == SqlTokenKind.Identifier && !token.IsQuoted &&
                    string.Equals(token.Value, words[offset], StringComparison.OrdinalIgnoreCase);
            }

            if (matched)
            {
                return words.Length;
            }
        }

        return 0;
    }

    /// <summary>名稱是內建型別或同義字時，型別的定義；同義字回它代表的那個型別。</summary>
    private static (string Name, Func<string> Description, bool TakesArguments)? Find(string name)
    {
        foreach (var (synonym, type) in Synonyms)
        {
            if (string.Equals(synonym, name, StringComparison.OrdinalIgnoreCase))
            {
                return Find(type);
            }
        }

        foreach (var definition in Definitions)
        {
            if (string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return definition;
            }
        }

        return null;
    }

    /// <summary>內建型別的建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> All => SuggestionCache.Current;

    private static IReadOnlyList<SqlSuggestion> Build()
    {
        var suggestions = new List<SqlSuggestion>(Definitions.Length + Synonyms.Length);

        foreach (var (name, describe, takesArguments) in Definitions.Concat(Synonyms.Select(Expand)))
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

    private static (string Name, Func<string> Description, bool TakesArguments) Expand((string Name, string Type) synonym)
    {
        var type = Find(synonym.Type)!.Value;
        return (synonym.Name, type.Description, type.TakesArguments);
    }
}
