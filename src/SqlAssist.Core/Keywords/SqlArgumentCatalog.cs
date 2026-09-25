using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 「這個位置文法上只有這幾個字合法」的封閉清單：日期部分與查詢提示。
/// </summary>
/// <remarks>
/// 三份都與內建函式、型別同一個處境——它們在文法上不是關鍵字，ScriptDom 的 token
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
        ("IGNORE_TRIGGERS", () => ArgumentText.TableHintIgnoreTriggers, false)
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
        ("USE HINT", () => ArgumentText.QueryHintUseHint, true),
        ("QUERYTRACEON", () => ArgumentText.QueryHintQuerytraceon, false),
        ("LABEL", () => ArgumentText.QueryHintLabel, false)
    };

    private static readonly SqlLanguageCache<IReadOnlyList<SqlSuggestion>> DatePartCache =
        new(_ => BuildDateParts());

    private static readonly SqlLanguageCache<IReadOnlyList<SqlSuggestion>> TableHintCache =
        new(_ => Build(TableHintDefinitions, SuggestionKind.TableHint));

    private static readonly SqlLanguageCache<IReadOnlyList<SqlSuggestion>> QueryHintCache =
        new(_ => Build(QueryHintDefinitions, SuggestionKind.QueryHint));

    /// <summary><c>DATEADD</c> 這一族第一個引數的建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> DateParts => DatePartCache.Current;

    /// <summary><c>WITH (…)</c> 的資料表提示建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> TableHints => TableHintCache.Current;

    /// <summary><c>OPTION (…)</c> 的查詢提示建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> QueryHints => QueryHintCache.Current;

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
                return TryFind(DatePartDefinitions, name, leading: false, out description);
            case SqlBuiltInKind.TableHint:
                return TryFind(TableHintDefinitions, name, leading: false, out description);
            case SqlBuiltInKind.QueryHint:
                return TryFind(QueryHintDefinitions, name, leading: false, out description);
            default:
                description = string.Empty;
                return false;
        }
    }

    /// <summary>這個名稱是不是三份封閉清單裡的字，或多字寫法的第一個詞。</summary>
    /// <remarks>
    /// 給滑鼠停留提示先擋一道用：那條路要判斷位置就得先做一次詞法分析，
    /// 而停在字上的絕大多數名稱根本不在這三份清單裡。
    ///
    /// 第一個詞也算，否則 <c>FORCE ORDER</c> 停在 <c>FORCE</c> 上會在這裡就被擋掉，
    /// 呼叫端沒有機會把後面那個詞接上去再查一次。
    /// </remarks>
    public static bool Contains(string? name)
    {
        return TryFind(DatePartDefinitions, name, leading: true, out _) ||
            TryFind(TableHintDefinitions, name, leading: true, out _) ||
            TryFind(QueryHintDefinitions, name, leading: true, out _);
    }

    private static bool TryFind(
        (string Name, Func<string> Description)[] definitions,
        string? name,
        bool leading,
        out string description)
    {
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var (candidate, value) in definitions)
            {
                if (Matches(candidate, name!, leading))
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
        bool leading,
        out string description)
    {
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var (candidate, value, _) in definitions)
            {
                if (Matches(candidate, name!, leading))
                {
                    description = value();
                    return true;
                }
            }
        }

        description = string.Empty;
        return false;
    }

    /// <param name="leading">
    /// 多字寫法的第一個詞算不算命中（<c>FORCE</c> 之於 <c>FORCE ORDER</c>）。
    /// </param>
    private static bool Matches(string candidate, string name, bool leading)
    {
        if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return leading &&
            candidate.Length > name.Length &&
            candidate[name.Length] == ' ' &&
            string.Compare(candidate, 0, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0;
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
