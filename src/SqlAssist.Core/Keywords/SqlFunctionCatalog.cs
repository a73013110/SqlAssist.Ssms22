using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// T-SQL 內建函式。
/// </summary>
/// <remarks>
/// 這一份是手寫的，而且只能手寫。關鍵字目錄由
/// <c>tools/Generate-Keywords.ps1</c> 反射 ScriptDom 的 token 列舉產生，
/// 但內建函式在文法上不是關鍵字——<c>COUNT</c>、<c>SUM</c>、<c>GETDATE</c>
/// 在 ScriptDom 眼中全都只是識別字，任何工具在這一塊都只能自己維護清單。
///
/// 與關鍵字重疊的名稱一律不收，而且是在執行期比對關鍵字目錄後排除，不是靠人記得：
/// <c>LEFT</c> 同時是 <c>LEFT JOIN</c> 與 <c>LEFT(字串, 長度)</c>，
/// 收進來會讓它只剩運算式位置，<c>LEFT JOIN</c> 就從清單裡消失了。
/// 少一個函式只是少一個補字，少一個 <c>JOIN</c> 是使用者打不出來。
///
/// 位置一律是運算式位置：語句開頭、資料來源位置與 DDL 物件位置不該冒出
/// <c>COUNT</c>。游標落在括號或運算子後面時分析器回報
/// <see cref="SqlKeywordPosition.Any"/>，交集仍然成立，
/// 所以 <c>SELECT COUNT(</c> 裡面照樣列得出來。
/// </remarks>
public static class SqlFunctionCatalog
{
    /// <summary>函式可以出現的位置。</summary>
    private const SqlKeywordPosition ExpressionPositions =
        SqlKeywordPosition.SelectList
        | SqlKeywordPosition.SelectListTail
        | SqlKeywordPosition.Predicate
        | SqlKeywordPosition.ExpressionTail
        | SqlKeywordPosition.OrderByTail
        | SqlKeywordPosition.CaseArm
        | SqlKeywordPosition.CaseBody;

    /// <summary>名稱與簽章；簽章同時當成清單右側的說明。</summary>
    private static readonly (string Name, string Signature)[] Definitions =
    {
        // 彙總
        ("AVG", "AVG(expression)"),
        ("CHECKSUM_AGG", "CHECKSUM_AGG(expression)"),
        ("COUNT", "COUNT(expression | *)"),
        ("COUNT_BIG", "COUNT_BIG(expression | *)"),
        ("GROUPING", "GROUPING(column)"),
        ("GROUPING_ID", "GROUPING_ID(column [, ...])"),
        ("MAX", "MAX(expression)"),
        ("MIN", "MIN(expression)"),
        ("STDEV", "STDEV(expression)"),
        ("STDEVP", "STDEVP(expression)"),
        ("STRING_AGG", "STRING_AGG(expression, separator)"),
        ("SUM", "SUM(expression)"),
        ("VAR", "VAR(expression)"),
        ("VARP", "VARP(expression)"),

        // 分析與排名
        ("DENSE_RANK", "DENSE_RANK() OVER (...)"),
        ("FIRST_VALUE", "FIRST_VALUE(expression) OVER (...)"),
        ("LAG", "LAG(expression [, offset [, default]]) OVER (...)"),
        ("LAST_VALUE", "LAST_VALUE(expression) OVER (...)"),
        ("LEAD", "LEAD(expression [, offset [, default]]) OVER (...)"),
        ("NTILE", "NTILE(groups) OVER (...)"),
        ("RANK", "RANK() OVER (...)"),
        ("ROW_NUMBER", "ROW_NUMBER() OVER (...)"),

        // 字串
        ("ASCII", "ASCII(character)"),
        ("CHAR", "CHAR(code)"),
        ("CHARINDEX", "CHARINDEX(needle, haystack [, start])"),
        ("CONCAT", "CONCAT(value1, value2 [, ...])"),
        ("CONCAT_WS", "CONCAT_WS(separator, value1, value2 [, ...])"),
        ("DATALENGTH", "DATALENGTH(expression)"),
        ("DIFFERENCE", "DIFFERENCE(value1, value2)"),
        ("FORMAT", "FORMAT(value, format [, culture])"),
        ("LEFT", "LEFT(value, length)"),
        ("LEN", "LEN(value)"),
        ("LOWER", "LOWER(value)"),
        ("LTRIM", "LTRIM(value)"),
        ("NCHAR", "NCHAR(code)"),
        ("PATINDEX", "PATINDEX(pattern, value)"),
        ("QUOTENAME", "QUOTENAME(value [, delimiter])"),
        ("REPLACE", "REPLACE(value, find, replaceWith)"),
        ("REPLICATE", "REPLICATE(value, count)"),
        ("REVERSE", "REVERSE(value)"),
        ("RIGHT", "RIGHT(value, length)"),
        ("RTRIM", "RTRIM(value)"),
        ("SOUNDEX", "SOUNDEX(value)"),
        ("SPACE", "SPACE(count)"),
        ("STR", "STR(number [, length [, decimals]])"),
        ("STRING_SPLIT", "STRING_SPLIT(value, separator)"),
        ("STUFF", "STUFF(value, start, length, replaceWith)"),
        ("SUBSTRING", "SUBSTRING(value, start, length)"),
        ("TRANSLATE", "TRANSLATE(value, characters, translations)"),
        ("TRIM", "TRIM(value)"),
        ("UNICODE", "UNICODE(character)"),
        ("UPPER", "UPPER(value)"),

        // 日期與時間
        ("DATEADD", "DATEADD(datepart, number, date)"),
        ("DATEDIFF", "DATEDIFF(datepart, startDate, endDate)"),
        ("DATEFROMPARTS", "DATEFROMPARTS(year, month, day)"),
        ("DATENAME", "DATENAME(datepart, date)"),
        ("DATEPART", "DATEPART(datepart, date)"),
        ("DAY", "DAY(date)"),
        ("EOMONTH", "EOMONTH(date [, monthsToAdd])"),
        ("GETDATE", "GETDATE()"),
        ("GETUTCDATE", "GETUTCDATE()"),
        ("ISDATE", "ISDATE(expression)"),
        ("MONTH", "MONTH(date)"),
        ("SWITCHOFFSET", "SWITCHOFFSET(datetimeoffset, timeZone)"),
        ("SYSDATETIME", "SYSDATETIME()"),
        ("SYSDATETIMEOFFSET", "SYSDATETIMEOFFSET()"),
        ("SYSUTCDATETIME", "SYSUTCDATETIME()"),
        ("TODATETIMEOFFSET", "TODATETIMEOFFSET(datetime, timeZone)"),
        ("YEAR", "YEAR(date)"),

        // 數值
        ("ABS", "ABS(number)"),
        ("CEILING", "CEILING(number)"),
        ("EXP", "EXP(number)"),
        ("FLOOR", "FLOOR(number)"),
        ("LOG", "LOG(number [, base])"),
        ("LOG10", "LOG10(number)"),
        ("POWER", "POWER(number, exponent)"),
        ("RAND", "RAND([seed])"),
        ("ROUND", "ROUND(number, length [, function])"),
        ("SIGN", "SIGN(number)"),
        ("SQRT", "SQRT(number)"),
        ("SQUARE", "SQUARE(number)"),

        // 轉換與空值。這一段刻意連 CONVERT、COALESCE、NULLIF、TRY_CONVERT 都寫進來
        // ——它們現在是 ScriptDom 認得的關鍵字，會被下面的重疊排除擋掉，
        // 但清單的意思是「這些是內建函式」，哪些同時是關鍵字交給比對去決定。
        ("CAST", "CAST(expression AS type)"),
        ("CHOOSE", "CHOOSE(index, value1, value2 [, ...])"),
        ("COALESCE", "COALESCE(value1, value2 [, ...])"),
        ("CONVERT", "CONVERT(type, expression [, style])"),
        ("IIF", "IIF(condition, whenTrue, whenFalse)"),
        ("ISNULL", "ISNULL(expression, replacement)"),
        ("ISNUMERIC", "ISNUMERIC(expression)"),
        ("NULLIF", "NULLIF(value1, value2)"),
        ("PARSE", "PARSE(value AS type [USING culture])"),
        ("TRY_CAST", "TRY_CAST(expression AS type)"),
        ("TRY_CONVERT", "TRY_CONVERT(type, expression [, style])"),
        ("TRY_PARSE", "TRY_PARSE(value AS type [USING culture])"),

        // 中繼資料與工作階段
        ("APP_NAME", "APP_NAME()"),
        ("BINARY_CHECKSUM", "BINARY_CHECKSUM(expression [, ...])"),
        ("CHECKSUM", "CHECKSUM(expression [, ...])"),
        ("DB_NAME", "DB_NAME([databaseId])"),
        ("HOST_NAME", "HOST_NAME()"),
        ("IDENT_CURRENT", "IDENT_CURRENT(tableName)"),
        ("NEWID", "NEWID()"),
        ("NEWSEQUENTIALID", "NEWSEQUENTIALID()"),
        ("OBJECT_ID", "OBJECT_ID(name [, type])"),
        ("OBJECT_NAME", "OBJECT_NAME(objectId)"),
        ("SCHEMA_NAME", "SCHEMA_NAME([schemaId])"),
        ("SCOPE_IDENTITY", "SCOPE_IDENTITY()"),
        ("SUSER_SNAME", "SUSER_SNAME([sid])"),
        ("USER_NAME", "USER_NAME([userId])"),

        // 錯誤處理
        ("ERROR_LINE", "ERROR_LINE()"),
        ("ERROR_MESSAGE", "ERROR_MESSAGE()"),
        ("ERROR_NUMBER", "ERROR_NUMBER()"),
        ("ERROR_PROCEDURE", "ERROR_PROCEDURE()"),
        ("ERROR_SEVERITY", "ERROR_SEVERITY()"),
        ("ERROR_STATE", "ERROR_STATE()"),

        // JSON
        ("ISJSON", "ISJSON(expression)"),
        ("JSON_MODIFY", "JSON_MODIFY(json, path, newValue)"),
        ("JSON_QUERY", "JSON_QUERY(json [, path])"),
        ("JSON_VALUE", "JSON_VALUE(json, path)"),
        ("OPENJSON", "OPENJSON(json [, path])")
    };

    private static IReadOnlyList<SqlSuggestion>? _suggestions;

    private static readonly object Gate = new();

    /// <summary>
    /// 自動大寫查得到的名稱。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="All"/> 分開建，因為排除的東西不一樣。這一份多排掉內建資料型別：
    /// <c>char</c> 同時是型別與函式，而型別刻意不做自動大寫
    /// （<c>SqlKeywordCatalog</c> 的 <c>DataTypes</c>），
    /// <c>CAST(x AS char(10))</c> 不該因為打了左括號就變成 <c>CHAR(10)</c>。
    ///
    /// 關鍵字也一併排掉，理由與建議清單那一份相同——那些字走關鍵字目錄那條路，
    /// 而且它們在那裡還帶著位置資訊。
    ///
    /// 這份是雜湊集合而不是走 <see cref="All"/> 找一遍：
    /// 查表在按鍵路徑上，每按一次左括號就問一次。
    /// </remarks>
    private static readonly HashSet<string> UppercaseNames = BuildUppercaseNames();

    /// <summary>
    /// 查出某個內建函式名稱的標準寫法。
    /// </summary>
    /// <remarks>
    /// 大小寫不敏感；不是內建函式、或同時是關鍵字或內建型別時回傳 false。
    /// 呼叫端只在使用者打的是左括號時才問（見 <c>SqlKeywordCase</c>）——
    /// <c>SELECT max FROM t</c> 的 <c>max</c> 可能是一個資料行的名字，
    /// 而 <c>max(</c> 在 T-SQL 裡只有一種意思。
    /// </remarks>
    public static bool TryGetCanonical(string word, out string canonical)
    {
        if (string.IsNullOrEmpty(word) || !UppercaseNames.Contains(word))
        {
            canonical = string.Empty;
            return false;
        }

        canonical = word.ToUpperInvariant();
        return true;
    }

    /// <summary>
    /// 內建函式的建議項。
    /// </summary>
    /// <remarks>
    /// 插入文字帶著左括號：這些名稱單獨出現一律是語法錯誤，
    /// 補上括號等於少按一次鍵，而游標剛好停在第一個引數上。
    /// </remarks>
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

    private static HashSet<string> BuildUppercaseNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, _) in Definitions)
        {
            if (!SqlKeywordCatalog.IsKeywordOrDataType(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static IReadOnlyList<SqlSuggestion> Build()
    {
        var suggestions = new List<SqlSuggestion>(Definitions.Length);

        foreach (var (name, signature) in Definitions)
        {
            if (SqlKeywordCatalog.IsKeyword(name))
            {
                continue;
            }

            suggestions.Add(new SqlSuggestion(
                name,
                name + "(",
                signature,
                signature,
                SuggestionKind.BuiltInFunction,
                positions: ExpressionPositions));
        }

        return suggestions;
    }
}
