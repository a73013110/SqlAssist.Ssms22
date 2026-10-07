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
    /// <c>CURRENT_TIME</c>、<c>CURRENT_TIMESTAMP</c> 的精確度可省，慣例寫法不帶括號。
    /// </remarks>
    private static readonly (string Name, Func<string> Description, bool TakesArguments)[] OdbcFunctionDefinitions =
    {
        // 字串
        ("ASCII", () => ArgumentText.OdbcAscii, true),
        ("BIT_LENGTH", () => ArgumentText.OdbcBitLength, true),
        ("CHAR", () => ArgumentText.OdbcChar, true),
        ("CHAR_LENGTH", () => ArgumentText.OdbcCharLength, true),
        ("CHARACTER_LENGTH", () => ArgumentText.OdbcCharLength, true),
        ("CONCAT", () => ArgumentText.OdbcConcat, true),
        ("DIFFERENCE", () => ArgumentText.OdbcDifference, true),
        ("INSERT", () => ArgumentText.OdbcInsert, true),
        ("LCASE", () => ArgumentText.OdbcLcase, true),
        ("LEFT", () => ArgumentText.OdbcLeft, true),
        ("LENGTH", () => ArgumentText.OdbcLength, true),
        ("LOCATE", () => ArgumentText.OdbcLocate, true),
        ("LTRIM", () => ArgumentText.OdbcLtrim, true),
        ("OCTET_LENGTH", () => ArgumentText.OdbcOctetLength, true),
        ("POSITION", () => ArgumentText.OdbcLocate, true),
        ("REPEAT", () => ArgumentText.OdbcRepeat, true),
        ("REPLACE", () => ArgumentText.OdbcReplace, true),
        ("RIGHT", () => ArgumentText.OdbcRight, true),
        ("RTRIM", () => ArgumentText.OdbcRtrim, true),
        ("SOUNDEX", () => ArgumentText.OdbcSoundex, true),
        ("SPACE", () => ArgumentText.OdbcSpace, true),
        ("SUBSTRING", () => ArgumentText.OdbcSubstring, true),
        ("UCASE", () => ArgumentText.OdbcUcase, true),

        // 數值
        ("ABS", () => ArgumentText.OdbcAbs, true),
        ("ACOS", () => ArgumentText.OdbcAcos, true),
        ("ASIN", () => ArgumentText.OdbcAsin, true),
        ("ATAN", () => ArgumentText.OdbcAtan, true),
        ("ATAN2", () => ArgumentText.OdbcAtan2, true),
        ("CEILING", () => ArgumentText.OdbcCeiling, true),
        ("COS", () => ArgumentText.OdbcCos, true),
        ("COT", () => ArgumentText.OdbcCot, true),
        ("DEGREES", () => ArgumentText.OdbcDegrees, true),
        ("EXP", () => ArgumentText.OdbcExp, true),
        ("FLOOR", () => ArgumentText.OdbcFloor, true),
        ("LOG", () => ArgumentText.OdbcLog, true),
        ("LOG10", () => ArgumentText.OdbcLog10, true),
        ("MOD", () => ArgumentText.OdbcMod, true),
        ("PI", () => ArgumentText.OdbcPi, true),
        ("POWER", () => ArgumentText.OdbcPower, true),
        ("RADIANS", () => ArgumentText.OdbcRadians, true),
        ("RAND", () => ArgumentText.OdbcRand, true),
        ("ROUND", () => ArgumentText.OdbcRound, true),
        ("SIGN", () => ArgumentText.OdbcSign, true),
        ("SIN", () => ArgumentText.OdbcSin, true),
        ("SQRT", () => ArgumentText.OdbcSqrt, true),
        ("TAN", () => ArgumentText.OdbcTan, true),
        ("TRUNCATE", () => ArgumentText.OdbcTruncate, true),

        // 日期、時間與間隔
        ("CURRENT_DATE", () => ArgumentText.OdbcCurrentDate, true),
        ("CURDATE", () => ArgumentText.OdbcCurrentDate, true),
        ("CURRENT_TIME", () => ArgumentText.OdbcCurrentTime, false),
        ("CURTIME", () => ArgumentText.OdbcCurrentTime, true),
        ("CURRENT_TIMESTAMP", () => ArgumentText.OdbcCurrentTimestamp, false),
        ("NOW", () => ArgumentText.OdbcCurrentTimestamp, true),
        ("DAYNAME", () => ArgumentText.OdbcDayname, true),
        ("DAYOFMONTH", () => ArgumentText.OdbcDayofmonth, true),
        ("DAYOFWEEK", () => ArgumentText.OdbcDayofweek, true),
        ("DAYOFYEAR", () => ArgumentText.OdbcDayofyear, true),
        ("EXTRACT", () => ArgumentText.OdbcExtract, true),
        ("HOUR", () => ArgumentText.OdbcHour, true),
        ("MINUTE", () => ArgumentText.OdbcMinute, true),
        ("MONTH", () => ArgumentText.OdbcMonth, true),
        ("MONTHNAME", () => ArgumentText.OdbcMonthname, true),
        ("QUARTER", () => ArgumentText.OdbcQuarter, true),
        ("SECOND", () => ArgumentText.OdbcSecond, true),
        ("TIMESTAMPADD", () => ArgumentText.OdbcTimestampadd, true),
        ("TIMESTAMPDIFF", () => ArgumentText.OdbcTimestampdiff, true),
        ("WEEK", () => ArgumentText.OdbcWeek, true),
        ("YEAR", () => ArgumentText.OdbcYear, true),

        // 系統與明確轉換
        ("DATABASE", () => ArgumentText.OdbcDatabase, true),
        ("IFNULL", () => ArgumentText.OdbcIfnull, true),
        ("USER", () => ArgumentText.OdbcUser, true),
        ("CONVERT", () => ArgumentText.OdbcConvert, true)
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
        new(_ => Build(OdbcFunctionDefinitions, SuggestionKind.BuiltInFunction));

    /// <summary><c>DATEADD</c> 這一族第一個引數的建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> DateParts => DatePartCache.Current;

    /// <summary><c>WITH (…)</c> 的資料表提示建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> TableHints => TableHintCache.Current;

    /// <summary><c>OPTION (…)</c> 的查詢提示建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> QueryHints => QueryHintCache.Current;

    /// <summary>ODBC <c>{fn </c> 之後的純量函式建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> OdbcFunctions => OdbcFunctionCache.Current;

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
