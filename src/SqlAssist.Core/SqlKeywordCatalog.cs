using System;
using System.Collections.Generic;

namespace SqlAssist.Core;

/// <summary>
/// T-SQL 關鍵字。
/// </summary>
/// <remarks>
/// 清單本身不手寫，由 <c>tools/Generate-Keywords.ps1</c> 反射 ScriptDom 產生
/// （見 <c>SqlKeywordCatalog.Generated.cs</c>）：字面值取自 <c>TSqlTokenType</c>
/// 並以 tokenizer 回驗，位置則由剖析器對樣板的判定決定。手寫的只剩兩件事——
/// 內建資料型別，以及自動大寫的例外。
///
/// 建議清單的雜訊改由 <see cref="SqlKeywordPosition"/> 控制，不再靠一份人工篩過的
/// 短清單。因此這裡不再區分「進清單的」與「只做大寫的」：180 個字都進得了清單，
/// 只是各自出現在文法允許的位置。
/// </remarks>
public static class SqlKeywordCatalog
{
    /// <summary>
    /// 不做自動大寫的關鍵字。
    /// </summary>
    /// <remarks>
    /// <c>GO</c> 是 SSMS 的批次分隔符而不是 T-SQL 關鍵字，而且兩個字母的字太容易
    /// 誤傷別名——<c>FROM Orders go</c> 這種寫法是合法的。它仍然會出現在建議清單裡，
    /// 只是打完不會被改寫。
    /// </remarks>
    private static readonly HashSet<string> UppercaseExclusions =
        new(StringComparer.OrdinalIgnoreCase) { "GO" };

    /// <summary>
    /// 內建資料型別。
    /// </summary>
    /// <remarks>
    /// 只用於語法著色，不進自動大寫：<c>int</c> 與 <c>INT</c> 都合法，
    /// 而使用者在指令碼裡怎麼寫型別是他自己的風格。
    /// 但著色不能因此把型別畫成一般文字——結構預覽裡的 CREATE TABLE
    /// 有一半的字是型別，全部變黑就等於沒有著色。
    ///
    /// 這份沒有跟著自動產生：ScriptDom 把型別名稱當識別字掃，token 列舉裡沒有它們。
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

    private static readonly Dictionary<string, SqlKeywordPosition> Positions = BuildPositions();

    private static readonly string[] AllKeywords = BuildAllKeywords();

    /// <summary>產生這份目錄所用的 ScriptDom 版本。</summary>
    public static string SourceVersion => SqlKeywordCatalogData.SourceVersion;

    /// <summary>全部關鍵字，已排序。</summary>
    public static IReadOnlyList<string> All => AllKeywords;

    /// <summary>出現在建議清單裡的關鍵字。</summary>
    /// <remarks>
    /// 現在等於 <see cref="All"/>：清單雜訊由位置過濾負責，不再靠縮短清單。
    /// </remarks>
    public static IReadOnlyList<string> SuggestionKeywords => AllKeywords;

    /// <summary>
    /// 查出某個關鍵字可以出現在哪些位置。
    /// </summary>
    /// <remarks>
    /// 產生器判不出位置的字（<c>FILLFACTOR</c>、<c>STOPLIST</c> 這類深層子句字）
    /// 回傳 <see cref="SqlKeywordPosition.Any"/>：寧可讓它在每個位置都出現，
    /// 也不要因為樣板沒涵蓋到就讓使用者永遠打不出來。
    /// </remarks>
    public static SqlKeywordPosition GetPositions(string keyword)
    {
        if (string.IsNullOrEmpty(keyword) || !Positions.TryGetValue(keyword, out var positions))
        {
            return SqlKeywordPosition.Any;
        }

        return positions == SqlKeywordPosition.None ? SqlKeywordPosition.Any : positions;
    }

    /// <summary>是否為認得的關鍵字或內建資料型別；語法著色用。</summary>
    public static bool IsKeywordOrDataType(string word)
    {
        return !string.IsNullOrEmpty(word)
            && (Positions.ContainsKey(word) || DataTypes.Contains(word));
    }

    /// <summary>是否為認得的關鍵字。</summary>
    public static bool IsKeyword(string word)
    {
        return !string.IsNullOrEmpty(word) && Positions.ContainsKey(word);
    }

    /// <summary>
    /// 查出某個字的標準寫法。
    /// </summary>
    /// <remarks>
    /// 大小寫不敏感；不是關鍵字、或屬於 <see cref="UppercaseExclusions"/> 時回傳 false。
    /// </remarks>
    public static bool TryGetCanonical(string word, out string canonical)
    {
        if (string.IsNullOrEmpty(word) || UppercaseExclusions.Contains(word))
        {
            canonical = string.Empty;
            return false;
        }

        if (!Positions.ContainsKey(word))
        {
            canonical = string.Empty;
            return false;
        }

        canonical = word.ToUpperInvariant();
        return true;
    }

    private static Dictionary<string, SqlKeywordPosition> BuildPositions()
    {
        var positions = new Dictionary<string, SqlKeywordPosition>(
            SqlKeywordCatalogData.Keywords.Length,
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in SqlKeywordCatalogData.Keywords)
        {
            positions[entry.Key] = entry.Value;
        }

        return positions;
    }

    private static string[] BuildAllKeywords()
    {
        var keywords = new string[SqlKeywordCatalogData.Keywords.Length];

        for (var index = 0; index < SqlKeywordCatalogData.Keywords.Length; index++)
        {
            keywords[index] = SqlKeywordCatalogData.Keywords[index].Key;
        }

        return keywords;
    }
}
