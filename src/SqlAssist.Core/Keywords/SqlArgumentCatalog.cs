using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 「這個位置文法上只有這幾個字合法」的封閉清單：日期部分、提示與 ODBC 純量函式。
/// </summary>
/// <remarks>
/// 這幾份都與內建函式、型別同一個處境——它們在文法上不是關鍵字，ScriptDom 的 token
/// 列舉撈不到，只能手寫。差別只在位置更窄：<c>DATEADD(</c> 的第一個引數、
/// <c>WITH (</c> 與 <c>OPTION (</c> 的括號裡，除了這幾個字沒有別的東西是對的。
///
/// 位置一律是 <see cref="SqlKeywordPosition.Any"/>：目標本身就已經把位置說完了，
/// 再判一次，判對沒有好處，判錯的代價是清單整個空掉。
/// </remarks>
public static class SqlArgumentCatalog
{
    /// <summary>
    /// <c>DATEADD</c> 這一族的第一個引數。
    /// </summary>
    /// <remarks>
    /// 只收完整名稱，不收 <c>yy</c>、<c>dd</c> 這些縮寫：縮寫背得起來的人不需要補字，
    /// 而 15 個名稱再乘上兩三種縮寫，清單就從「一眼看完」變成要捲動。
    /// </remarks>
    private static readonly (string Name, Func<string> Description)[] DatePartDefinitions =
    {
        ("YEAR", () => ArgumentText.DatePartYear),
        ("QUARTER", () => ArgumentText.DatePartQuarter),
        ("MONTH", () => ArgumentText.DatePartMonth),
        ("DAYOFYEAR", () => ArgumentText.DatePartDayofyear),
        ("DAY", () => ArgumentText.DatePartDay),
        ("WEEK", () => ArgumentText.DatePartWeek),
        ("WEEKDAY", () => ArgumentText.DatePartWeekday),
        ("HOUR", () => ArgumentText.DatePartHour),
        ("MINUTE", () => ArgumentText.DatePartMinute),
        ("SECOND", () => ArgumentText.DatePartSecond),
        ("MILLISECOND", () => ArgumentText.DatePartMillisecond),
        ("MICROSECOND", () => ArgumentText.DatePartMicrosecond),
        ("NANOSECOND", () => ArgumentText.DatePartNanosecond),
        ("TZOFFSET", () => ArgumentText.DatePartTzoffset),
        ("ISO_WEEK", () => ArgumentText.DatePartIsoWeek)
    };

    /// <summary>
    /// <c>WITH (…)</c> 的資料表提示。
    /// </summary>
    /// <remarks>
    /// <c>INDEX</c> 帶左括號提交（<c>INDEX(</c>）：它後面一定要接索引名稱或編號，
    /// 與內建函式同一個道理。
    /// </remarks>
    private static readonly (string Name, Func<string> Description, bool TakesArguments)[] TableHintDefinitions =
    {
        ("NOLOCK", () => ArgumentText.TableHintNolock, false),
        ("READUNCOMMITTED", () => ArgumentText.TableHintReaduncommitted, false),
        ("READCOMMITTED", () => ArgumentText.TableHintReadcommitted, false),
        ("REPEATABLEREAD", () => ArgumentText.TableHintRepeatableread, false),
        ("SERIALIZABLE", () => ArgumentText.TableHintSerializable, false),
        ("READPAST", () => ArgumentText.TableHintReadpast, false),
        ("ROWLOCK", () => ArgumentText.TableHintRowlock, false),
        ("PAGLOCK", () => ArgumentText.TableHintPaglock, false),
        ("TABLOCK", () => ArgumentText.TableHintTablock, false),
        ("TABLOCKX", () => ArgumentText.TableHintTablockx, false),
        ("UPDLOCK", () => ArgumentText.TableHintUpdlock, false),
        ("XLOCK", () => ArgumentText.TableHintXlock, false),
        ("HOLDLOCK", () => ArgumentText.TableHintHoldlock, false),
        ("NOEXPAND", () => ArgumentText.TableHintNoexpand, false),
        ("FORCESEEK", () => ArgumentText.TableHintForceseek, false),
        ("FORCESCAN", () => ArgumentText.TableHintForcescan, false),
        ("INDEX", () => ArgumentText.TableHintIndex, true),
        ("KEEPIDENTITY", () => ArgumentText.TableHintKeepidentity, false),
        ("KEEPDEFAULTS", () => ArgumentText.TableHintKeepdefaults, false),
        ("IGNORE_CONSTRAINTS", () => ArgumentText.TableHintIgnoreConstraints, false),
        ("IGNORE_TRIGGERS", () => ArgumentText.TableHintIgnoreTriggers, false),
        ("NOWAIT", () => ArgumentText.TableHintNowait, false),
        ("SNAPSHOT", () => ArgumentText.TableHintSnapshot, false),
        ("READCOMMITTEDLOCK", () => ArgumentText.TableHintReadcommittedlock, false)
    };

    /// <summary><c>OPTION (…)</c> 的查詢提示。</summary>
    private static readonly (string Name, Func<string> Description, bool TakesArguments)[] QueryHintDefinitions =
    {
        ("RECOMPILE", () => ArgumentText.QueryHintRecompile, false),
        ("OPTIMIZE FOR", () => ArgumentText.QueryHintOptimizeFor, false),
        ("OPTIMIZE FOR UNKNOWN", () => ArgumentText.QueryHintOptimizeForUnknown, false),
        ("MAXDOP", () => ArgumentText.QueryHintMaxdop, false),
        ("MAXRECURSION", () => ArgumentText.QueryHintMaxrecursion, false),
        ("FAST", () => ArgumentText.QueryHintFast, false),
        ("FORCE ORDER", () => ArgumentText.QueryHintForceOrder, false),
        ("KEEP PLAN", () => ArgumentText.QueryHintKeepPlan, false),
        ("KEEPFIXED PLAN", () => ArgumentText.QueryHintKeepfixedPlan, false),
        ("ROBUST PLAN", () => ArgumentText.QueryHintRobustPlan, false),
        ("EXPAND VIEWS", () => ArgumentText.QueryHintExpandViews, false),
        ("LOOP JOIN", () => ArgumentText.QueryHintLoopJoin, false),
        ("MERGE JOIN", () => ArgumentText.QueryHintMergeJoin, false),
        ("HASH JOIN", () => ArgumentText.QueryHintHashJoin, false),
        ("HASH GROUP", () => ArgumentText.QueryHintHashGroup, false),
        ("ORDER GROUP", () => ArgumentText.QueryHintOrderGroup, false),
        ("CONCAT UNION", () => ArgumentText.QueryHintConcatUnion, false),
        ("HASH UNION", () => ArgumentText.QueryHintHashUnion, false),
        ("MERGE UNION", () => ArgumentText.QueryHintMergeUnion, false),
        ("PARAMETERIZATION SIMPLE", () => ArgumentText.QueryHintParameterizationSimple, false),
        ("PARAMETERIZATION FORCED", () => ArgumentText.QueryHintParameterizationForced, false),
        ("NO_PERFORMANCE_SPOOL", () => ArgumentText.QueryHintNoPerformanceSpool, false),
        ("MAX_GRANT_PERCENT", () => ArgumentText.QueryHintMaxGrantPercent, false),
        ("MIN_GRANT_PERCENT", () => ArgumentText.QueryHintMinGrantPercent, false),
        ("USE PLAN", () => ArgumentText.QueryHintUsePlan, false),
        ("TABLE HINT", () => ArgumentText.QueryHintTableHint, true),
        ("DISABLE_OPTIMIZED_PLAN_FORCING", () => ArgumentText.QueryHintDisableOptimizedPlanForcing, false),
        ("IGNORE_NONCLUSTERED_COLUMNSTORE_INDEX", () => ArgumentText.QueryHintIgnoreNonclusteredColumnstoreIndex, false),
        ("USE HINT", () => ArgumentText.QueryHintUseHint, true),
        ("QUERYTRACEON", () => ArgumentText.QueryHintQuerytraceon, false),
        ("LABEL", () => ArgumentText.QueryHintLabel, false)
    };

    /// <summary>ODBC 跳脫 <c>{fn …}</c> 的純量函式。</summary>
    /// <remarks>
    /// 收 ODBC 附錄 E 的整份：sql-docs「ODBC Scalar Functions」說這些由引擎解譯，表格只列與 T-SQL 不重複的幾個，
    /// 只收表格的話 <c>{fn USER()}</c>、<c>{fn RIGHT(…)}</c> 列不出來。
    /// 簽章照附錄 E，與 <see cref="SqlFunctionCatalog"/> 同一種寫法：帶不帶括號提交由它說
    /// （<c>CURRENT_TIME</c>、<c>CURRENT_TIMESTAMP</c> 的精確度可省，慣例寫法不帶括號），
    /// 引數自有文法的也由它說（<c>EXTRACT(… FROM …)</c>，見 <see cref="TryGetOdbcSignature"/>）。
    /// </remarks>
    private static readonly (string Name, string Signature, Func<string> Description)[] OdbcFunctionDefinitions =
    {
        // 字串
        ("ASCII", "ASCII(string_exp)", () => ArgumentText.OdbcAscii),
        ("BIT_LENGTH", "BIT_LENGTH(string_exp)", () => ArgumentText.OdbcBitLength),
        ("CHAR", "CHAR(code)", () => ArgumentText.OdbcChar),
        ("CHAR_LENGTH", "CHAR_LENGTH(string_exp)", () => ArgumentText.OdbcCharLength),
        ("CHARACTER_LENGTH", "CHARACTER_LENGTH(string_exp)", () => ArgumentText.OdbcCharLength),
        ("CONCAT", "CONCAT(string_exp1, string_exp2)", () => ArgumentText.OdbcConcat),
        ("DIFFERENCE", "DIFFERENCE(string_exp1, string_exp2)", () => ArgumentText.OdbcDifference),
        ("INSERT", "INSERT(string_exp1, start, length, string_exp2)", () => ArgumentText.OdbcInsert),
        ("LCASE", "LCASE(string_exp)", () => ArgumentText.OdbcLcase),
        ("LEFT", "LEFT(string_exp, count)", () => ArgumentText.OdbcLeft),
        ("LENGTH", "LENGTH(string_exp)", () => ArgumentText.OdbcLength),
        ("LOCATE", "LOCATE(string_exp1, string_exp2[, start])", () => ArgumentText.OdbcLocate),
        ("LTRIM", "LTRIM(string_exp)", () => ArgumentText.OdbcLtrim),
        ("OCTET_LENGTH", "OCTET_LENGTH(string_exp)", () => ArgumentText.OdbcOctetLength),
        ("POSITION", "POSITION(character_exp IN character_exp)", () => ArgumentText.OdbcLocate),
        ("REPEAT", "REPEAT(string_exp, count)", () => ArgumentText.OdbcRepeat),
        ("REPLACE", "REPLACE(string_exp1, string_exp2, string_exp3)", () => ArgumentText.OdbcReplace),
        ("RIGHT", "RIGHT(string_exp, count)", () => ArgumentText.OdbcRight),
        ("RTRIM", "RTRIM(string_exp)", () => ArgumentText.OdbcRtrim),
        ("SOUNDEX", "SOUNDEX(string_exp)", () => ArgumentText.OdbcSoundex),
        ("SPACE", "SPACE(count)", () => ArgumentText.OdbcSpace),
        ("SUBSTRING", "SUBSTRING(string_exp, start, length)", () => ArgumentText.OdbcSubstring),
        ("UCASE", "UCASE(string_exp)", () => ArgumentText.OdbcUcase),

        // 數值
        ("ABS", "ABS(numeric_exp)", () => ArgumentText.OdbcAbs),
        ("ACOS", "ACOS(float_exp)", () => ArgumentText.OdbcAcos),
        ("ASIN", "ASIN(float_exp)", () => ArgumentText.OdbcAsin),
        ("ATAN", "ATAN(float_exp)", () => ArgumentText.OdbcAtan),
        ("ATAN2", "ATAN2(float_exp1, float_exp2)", () => ArgumentText.OdbcAtan2),
        ("CEILING", "CEILING(numeric_exp)", () => ArgumentText.OdbcCeiling),
        ("COS", "COS(float_exp)", () => ArgumentText.OdbcCos),
        ("COT", "COT(float_exp)", () => ArgumentText.OdbcCot),
        ("DEGREES", "DEGREES(numeric_exp)", () => ArgumentText.OdbcDegrees),
        ("EXP", "EXP(float_exp)", () => ArgumentText.OdbcExp),
        ("FLOOR", "FLOOR(numeric_exp)", () => ArgumentText.OdbcFloor),
        ("LOG", "LOG(float_exp)", () => ArgumentText.OdbcLog),
        ("LOG10", "LOG10(float_exp)", () => ArgumentText.OdbcLog10),
        ("MOD", "MOD(integer_exp1, integer_exp2)", () => ArgumentText.OdbcMod),
        ("PI", "PI()", () => ArgumentText.OdbcPi),
        ("POWER", "POWER(numeric_exp, integer_exp)", () => ArgumentText.OdbcPower),
        ("RADIANS", "RADIANS(numeric_exp)", () => ArgumentText.OdbcRadians),
        ("RAND", "RAND([integer_exp])", () => ArgumentText.OdbcRand),
        ("ROUND", "ROUND(numeric_exp, integer_exp)", () => ArgumentText.OdbcRound),
        ("SIGN", "SIGN(numeric_exp)", () => ArgumentText.OdbcSign),
        ("SIN", "SIN(float_exp)", () => ArgumentText.OdbcSin),
        ("SQRT", "SQRT(float_exp)", () => ArgumentText.OdbcSqrt),
        ("TAN", "TAN(float_exp)", () => ArgumentText.OdbcTan),
        ("TRUNCATE", "TRUNCATE(numeric_exp, integer_exp)", () => ArgumentText.OdbcTruncate),

        // 日期、時間與間隔
        ("CURRENT_DATE", "CURRENT_DATE()", () => ArgumentText.OdbcCurrentDate),
        ("CURDATE", "CURDATE()", () => ArgumentText.OdbcCurrentDate),
        ("CURRENT_TIME", "CURRENT_TIME[(time_precision)]", () => ArgumentText.OdbcCurrentTime),
        ("CURTIME", "CURTIME()", () => ArgumentText.OdbcCurrentTime),
        ("CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP[(timestamp_precision)]", () => ArgumentText.OdbcCurrentTimestamp),
        ("NOW", "NOW()", () => ArgumentText.OdbcCurrentTimestamp),
        ("DAYNAME", "DAYNAME(date_exp)", () => ArgumentText.OdbcDayname),
        ("DAYOFMONTH", "DAYOFMONTH(date_exp)", () => ArgumentText.OdbcDayofmonth),
        ("DAYOFWEEK", "DAYOFWEEK(date_exp)", () => ArgumentText.OdbcDayofweek),
        ("DAYOFYEAR", "DAYOFYEAR(date_exp)", () => ArgumentText.OdbcDayofyear),
        ("EXTRACT", "EXTRACT(extract_field FROM extract_source)", () => ArgumentText.OdbcExtract),
        ("HOUR", "HOUR(time_exp)", () => ArgumentText.OdbcHour),
        ("MINUTE", "MINUTE(time_exp)", () => ArgumentText.OdbcMinute),
        ("MONTH", "MONTH(date_exp)", () => ArgumentText.OdbcMonth),
        ("MONTHNAME", "MONTHNAME(date_exp)", () => ArgumentText.OdbcMonthname),
        ("QUARTER", "QUARTER(date_exp)", () => ArgumentText.OdbcQuarter),
        ("SECOND", "SECOND(time_exp)", () => ArgumentText.OdbcSecond),
        ("TIMESTAMPADD", "TIMESTAMPADD(interval, integer_exp, timestamp_exp)", () => ArgumentText.OdbcTimestampadd),
        ("TIMESTAMPDIFF", "TIMESTAMPDIFF(interval, timestamp_exp1, timestamp_exp2)", () => ArgumentText.OdbcTimestampdiff),
        ("WEEK", "WEEK(date_exp)", () => ArgumentText.OdbcWeek),
        ("YEAR", "YEAR(date_exp)", () => ArgumentText.OdbcYear),

        // 系統與明確轉換
        ("DATABASE", "DATABASE()", () => ArgumentText.OdbcDatabase),
        ("IFNULL", "IFNULL(exp, value)", () => ArgumentText.OdbcIfnull),
        ("USER", "USER()", () => ArgumentText.OdbcUser),
        ("CONVERT", "CONVERT(value_exp, data_type)", () => ArgumentText.OdbcConvert)
    };

    /// <summary>三份封閉清單裡的名稱拆成的字；多字寫法的每一個字都在內。</summary>
    private static readonly HashSet<string> Words = CollectWords();

    private static readonly SqlLanguageCache<IReadOnlyList<SqlSuggestion>> DatePartCache =
        new(_ => BuildDateParts());

    private static readonly SqlLanguageCache<IReadOnlyList<SqlSuggestion>> TableHintCache =
        new(_ => Build(TableHintDefinitions, SuggestionKind.TableHint));

    private static readonly SqlLanguageCache<IReadOnlyList<SqlSuggestion>> QueryHintCache =
        new(_ => Build(QueryHintDefinitions, SuggestionKind.QueryHint));

    private static readonly SqlLanguageCache<IReadOnlyList<SqlSuggestion>> OdbcFunctionCache =
        new(_ => BuildOdbcFunctions());

    /// <summary><c>DATEADD</c> 這一族第一個引數的建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> DateParts => DatePartCache.Current;

    /// <summary><c>WITH (…)</c> 的資料表提示建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> TableHints => TableHintCache.Current;

    /// <summary><c>OPTION (…)</c> 的查詢提示建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> QueryHints => QueryHintCache.Current;

    /// <summary>ODBC <c>{fn </c> 之後的純量函式建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> OdbcFunctions => OdbcFunctionCache.Current;

    /// <summary>查出 ODBC 純量函式的簽章；大小寫不敏感。</summary>
    /// <remarks>
    /// 與 T-SQL 同名的（<c>LEFT</c>、<c>CONVERT</c>）引數不一定一樣，<c>{fn</c> 之後的呼叫只問這一份。
    /// </remarks>
    public static bool TryGetOdbcSignature(string? name, out string signature)
    {
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var (candidate, value, _) in OdbcFunctionDefinitions)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                {
                    signature = value;
                    return true;
                }
            }
        }

        signature = string.Empty;
        return false;
    }

    /// <summary>
    /// 查出一個提示或日期部分的一行說明；大小寫不敏感。
    /// </summary>
    /// <remarks>
    /// 這一行是它們說明的唯一出處，滑鼠停留提示與建議清單問的是同一份
    /// （<see cref="SqlBuiltInDocCatalog"/>）。抄進內建說明資源的症狀是改了一邊
    /// 另一邊沒改，而兩邊都看得見。
    ///
    /// 種類由呼叫端指定而不是三份一起找：<c>WITH (MAXDOP)</c> 的 <c>MAXDOP</c>
    /// 是查詢提示寫錯了位置，不是資料表提示，答得出來反而是提示自己編的。
    /// 線性掃過的理由同 <see cref="SqlDataTypeCatalog.TryGetDescription"/>。
    /// </remarks>
    public static bool TryGetDescription(string? name, SqlBuiltInKind kind, out string description)
    {
        switch (kind)
        {
            case SqlBuiltInKind.DatePart:
                return TryFind(DatePartDefinitions, name, out description);
            case SqlBuiltInKind.TableHint:
                return TryFind(TableHintDefinitions, name, out description);
            case SqlBuiltInKind.QueryHint:
                return TryFind(QueryHintDefinitions, name, out description);
            default:
                description = string.Empty;
                return false;
        }
    }

    /// <summary>這個字是不是三份封閉清單裡某個名稱的一個字。</summary>
    /// <remarks>
    /// 給滑鼠停留提示先擋一道用：那條路要判斷位置就得先做一次詞法分析，
    /// 而停在字上的絕大多數名稱根本不在這三份清單裡。
    ///
    /// 多字寫法的每一個字都算，否則 <c>FORCE ORDER</c> 停在 <c>FORCE</c> 或 <c>ORDER</c> 上
    /// 會在這裡就被擋掉，呼叫端沒有機會把前後的字併起來再查一次。
    /// </remarks>
    public static bool ContainsWord(string? word) => !string.IsNullOrEmpty(word) && Words.Contains(word!);

    /// <summary>這個名稱是不是一個資料表提示（<c>NOLOCK</c>、<c>INDEX</c>）。</summary>
    public static bool IsTableHint(string? name) => TryFind(TableHintDefinitions, name, out _);

    private static bool TryFind(
        (string Name, Func<string> Description)[] definitions,
        string? name,
        out string description)
    {
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var (candidate, value) in definitions)
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

    private static bool TryFind(
        (string Name, Func<string> Description, bool TakesArguments)[] definitions,
        string? name,
        out string description)
    {
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var (candidate, value, _) in definitions)
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

    private static HashSet<string> CollectWords()
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, _) in DatePartDefinitions)
        {
            words.UnionWith(name.Split(' '));
        }

        foreach (var (name, _, _) in TableHintDefinitions)
        {
            words.UnionWith(name.Split(' '));
        }

        foreach (var (name, _, _) in QueryHintDefinitions)
        {
            words.UnionWith(name.Split(' '));
        }

        return words;
    }

    private static IReadOnlyList<SqlSuggestion> BuildDateParts()
    {
        var suggestions = new List<SqlSuggestion>(DatePartDefinitions.Length);

        foreach (var (name, describe) in DatePartDefinitions)
        {
            var description = describe();

            suggestions.Add(new SqlSuggestion(
                name,
                name,
                description,
                description,
                SuggestionKind.DatePart));
        }

        return suggestions;
    }

    private static IReadOnlyList<SqlSuggestion> BuildOdbcFunctions()
    {
        var suggestions = new List<SqlSuggestion>(OdbcFunctionDefinitions.Length);

        foreach (var (name, signature, describe) in OdbcFunctionDefinitions)
        {
            var description = describe();
            var takesArguments = signature.Length > name.Length && signature[name.Length] == '(';

            suggestions.Add(new SqlSuggestion(
                name,
                takesArguments ? name + "(" : name,
                description,
                description,
                SuggestionKind.BuiltInFunction));
        }

        return suggestions;
    }

    private static IReadOnlyList<SqlSuggestion> Build(
        (string Name, Func<string> Description, bool TakesArguments)[] definitions,
        SuggestionKind kind)
    {
        var suggestions = new List<SqlSuggestion>(definitions.Length);

        foreach (var (name, describe, takesArguments) in definitions)
        {
            var description = describe();

            suggestions.Add(new SqlSuggestion(
                name,
                takesArguments ? name + "(" : name,
                description,
                description,
                kind));
        }

        return suggestions;
    }
}
