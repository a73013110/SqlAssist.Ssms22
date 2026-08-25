using System;
using System.Collections.Generic;

namespace SqlAssist.Core;

/// <summary>
/// T-SQL 關鍵字。
/// </summary>
/// <remarks>
/// 刻意分成兩份：
/// <see cref="SuggestionKeywords"/> 是會出現在建議清單裡的常用字，清單的雜訊要控制；
/// 自動大寫則對<b>所有</b>認得的關鍵字生效——使用者打了 <c>desc</c> 就是要 <c>DESC</c>，
/// 沒有理由因為它不在建議清單裡就不處理。
/// </remarks>
public static class SqlKeywordCatalog
{
    /// <summary>出現在建議清單裡的關鍵字。</summary>
    public static IReadOnlyList<string> SuggestionKeywords { get; } = new[]
    {
        "ALTER", "AND", "AS", "BEGIN", "BY", "CASE", "CREATE", "CROSS",
        "DECLARE", "DELETE", "DISTINCT", "DROP", "ELSE", "END", "EXEC",
        "EXECUTE", "EXISTS", "FROM", "FULL", "FUNCTION", "GROUP", "HAVING",
        "IF", "IN", "INNER", "INSERT", "INTO", "JOIN", "LEFT", "MERGE",
        "NOT", "NULL", "ON", "OR", "ORDER", "OUTER", "PROCEDURE", "RETURN",
        "RIGHT", "SELECT", "SET", "TABLE", "THEN", "TOP", "UNION", "UPDATE",
        "VALUES", "VIEW", "WHEN", "WHERE", "WITH"
    };

    /// <summary>只做自動大寫、不進建議清單的其餘關鍵字。</summary>
    private static readonly string[] AdditionalKeywords =
    {
        "ADD", "ALL", "ANY", "APPLY", "ASC", "BETWEEN", "BREAK", "CASCADE",
        "CATCH", "CHECK", "COLLATE", "COLUMN", "COMMIT", "CONSTRAINT",
        "CONTINUE", "CURSOR", "DATABASE", "DEFAULT", "DESC", "ESCAPE",
        "EXCEPT", "FETCH", "FOR", "FOREIGN", "GOTO", "GRANT", "IDENTITY",
        "INDEX", "INTERSECT", "IS", "KEY", "LIKE", "NEXT", "NOLOCK",
        "OFFSET", "OPTION", "OUTPUT", "OVER", "PARTITION", "PERCENT",
        "PIVOT", "PRIMARY", "PRINT", "RAISERROR", "REFERENCES", "REVERT",
        "ROLLBACK", "ROWS", "SCHEMA", "THROW", "TRANSACTION", "TRIGGER",
        "TRUNCATE", "TRY", "UNIQUE", "UNPIVOT", "USE", "USING", "WHILE"
    };

    /// <summary>
    /// 內建資料型別。
    /// </summary>
    /// <remarks>
    /// 只用於語法著色，不進自動大寫：<c>int</c> 與 <c>INT</c> 都合法，
    /// 而使用者在指令碼裡怎麼寫型別是他自己的風格。
    /// 但著色不能因此把型別畫成一般文字——結構預覽裡的 CREATE TABLE
    /// 有一半的字是型別，全部變黑就等於沒有著色。
    /// </remarks>
    private static readonly HashSet<string> DataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIGINT", "BINARY", "BIT", "CHAR", "DATE", "DATETIME", "DATETIME2",
        "DATETIMEOFFSET", "DECIMAL", "FLOAT", "GEOGRAPHY", "GEOMETRY",
        "HIERARCHYID", "IMAGE", "INT", "MONEY", "NCHAR", "NTEXT", "NUMERIC",
        "NVARCHAR", "REAL", "ROWVERSION", "SMALLDATETIME", "SMALLINT",
        "SMALLMONEY", "SQL_VARIANT", "SYSNAME", "TEXT", "TIME", "TIMESTAMP",
        "TINYINT", "UNIQUEIDENTIFIER", "VARBINARY", "VARCHAR", "XML"
    };

    private static readonly Dictionary<string, string> Canonical = BuildCanonical();

    /// <summary>是否為認得的關鍵字或內建資料型別；語法著色用。</summary>
    public static bool IsKeywordOrDataType(string word)
    {
        return !string.IsNullOrEmpty(word)
            && (Canonical.ContainsKey(word) || DataTypes.Contains(word));
    }

    /// <summary>
    /// 查出某個字的標準寫法。
    /// </summary>
    /// <remarks>
    /// 大小寫不敏感；不是關鍵字時回傳 false。刻意不處理 <c>GO</c>：
    /// 那是 SSMS 的批次分隔符而不是 T-SQL 關鍵字，而且兩個字母的字太容易
    /// 誤傷別名（<c>FROM Orders go</c> 這種寫法是合法的別名）。
    /// </remarks>
    public static bool TryGetCanonical(string word, out string canonical)
    {
        if (string.IsNullOrEmpty(word))
        {
            canonical = string.Empty;
            return false;
        }

        return Canonical.TryGetValue(word, out canonical!);
    }

    private static Dictionary<string, string> BuildCanonical()
    {
        var canonical = new Dictionary<string, string>(
            SuggestionKeywords.Count + AdditionalKeywords.Length,
            StringComparer.OrdinalIgnoreCase);

        foreach (var keyword in SuggestionKeywords)
        {
            canonical[keyword] = keyword;
        }

        foreach (var keyword in AdditionalKeywords)
        {
            canonical[keyword] = keyword;
        }

        return canonical;
    }
}
