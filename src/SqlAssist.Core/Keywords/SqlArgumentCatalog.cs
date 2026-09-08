using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;

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
    private static readonly (string Name, string Description)[] DatePartDefinitions =
    {
        ("YEAR", "年"),
        ("QUARTER", "季"),
        ("MONTH", "月"),
        ("DAYOFYEAR", "一年中的第幾天"),
        ("DAY", "日"),
        ("WEEK", "一年中的第幾週"),
        ("WEEKDAY", "星期幾"),
        ("HOUR", "時"),
        ("MINUTE", "分"),
        ("SECOND", "秒"),
        ("MILLISECOND", "毫秒"),
        ("MICROSECOND", "微秒"),
        ("NANOSECOND", "奈秒"),
        ("TZOFFSET", "時區位移（分鐘）"),
        ("ISO_WEEK", "ISO 8601 的第幾週")
    };

    /// <summary>
    /// <c>WITH (…)</c> 的資料表提示。
    /// </summary>
    /// <remarks>
    /// <c>INDEX</c> 帶左括號提交（<c>INDEX(</c>）：它後面一定要接索引名稱或編號，
    /// 與內建函式同一個道理。
    /// </remarks>
    private static readonly (string Name, string Description, bool TakesArguments)[] TableHintDefinitions =
    {
        ("NOLOCK", "不加共用鎖定，可能讀到未認可的資料", false),
        ("READUNCOMMITTED", "同 NOLOCK", false),
        ("READCOMMITTED", "以認可讀取隔離等級讀取", false),
        ("REPEATABLEREAD", "以可重複讀取隔離等級讀取", false),
        ("SERIALIZABLE", "以可序列化隔離等級讀取", false),
        ("READPAST", "跳過被鎖住的資料列", false),
        ("ROWLOCK", "強制使用資料列鎖定", false),
        ("PAGLOCK", "強制使用頁面鎖定", false),
        ("TABLOCK", "強制使用資料表鎖定", false),
        ("TABLOCKX", "強制使用資料表獨佔鎖定", false),
        ("UPDLOCK", "讀取時就取得更新鎖定", false),
        ("XLOCK", "取得獨佔鎖定", false),
        ("HOLDLOCK", "同 SERIALIZABLE", false),
        ("NOEXPAND", "索引檢視不展開成底層資料表", false),
        ("FORCESEEK", "強制以索引搜尋存取", false),
        ("FORCESCAN", "強制以掃描存取", false),
        ("INDEX", "指定要用的索引", true),
        ("KEEPIDENTITY", "大量插入時保留來源的識別值", false),
        ("KEEPDEFAULTS", "大量插入時保留資料行預設值", false),
        ("IGNORE_CONSTRAINTS", "大量插入時略過條件約束", false),
        ("IGNORE_TRIGGERS", "大量插入時略過觸發程序", false)
    };

    /// <summary><c>OPTION (…)</c> 的查詢提示。</summary>
    private static readonly (string Name, string Description, bool TakesArguments)[] QueryHintDefinitions =
    {
        ("RECOMPILE", "這一次執行重新編譯，不快取計畫", false),
        ("OPTIMIZE FOR", "指定編譯計畫時假設的參數值", false),
        ("OPTIMIZE FOR UNKNOWN", "以統計資料的平均值編譯，不看實際參數", false),
        ("MAXDOP", "限制平行處理原則的最大程度", false),
        ("MAXRECURSION", "限制遞迴 CTE 的最大層數", false),
        ("FAST", "先傳回前 N 列的計畫", false),
        ("FORCE ORDER", "照查詢寫的順序聯結", false),
        ("KEEP PLAN", "放寬重新編譯的門檻", false),
        ("KEEPFIXED PLAN", "統計資料改變時不重新編譯", false),
        ("ROBUST PLAN", "選一個容納得下最大資料列的計畫", false),
        ("EXPAND VIEWS", "索引檢視展開成底層資料表", false),
        ("LOOP JOIN", "只用巢狀迴圈聯結", false),
        ("MERGE JOIN", "只用合併聯結", false),
        ("HASH JOIN", "只用雜湊聯結", false),
        ("USE HINT", "套用具名的查詢提示", true),
        ("QUERYTRACEON", "為這一次編譯開啟追蹤旗標", false),
        ("LABEL", "為這個查詢加上標籤", false)
    };

    private static IReadOnlyList<SqlSuggestion>? _dateParts;

    private static IReadOnlyList<SqlSuggestion>? _tableHints;

    private static IReadOnlyList<SqlSuggestion>? _queryHints;

    private static readonly object Gate = new();

    /// <summary><c>DATEADD</c> 這一族第一個引數的建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> DateParts
    {
        get
        {
            lock (Gate)
            {
                return _dateParts ??= BuildDateParts();
            }
        }
    }

    /// <summary><c>WITH (…)</c> 的資料表提示建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> TableHints
    {
        get
        {
            lock (Gate)
            {
                return _tableHints ??= Build(TableHintDefinitions, SuggestionKind.TableHint);
            }
        }
    }

    /// <summary><c>OPTION (…)</c> 的查詢提示建議項。</summary>
    public static IReadOnlyList<SqlSuggestion> QueryHints
    {
        get
        {
            lock (Gate)
            {
                return _queryHints ??= Build(QueryHintDefinitions, SuggestionKind.QueryHint);
            }
        }
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
        (string Name, string Description)[] definitions,
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
                    description = value;
                    return true;
                }
            }
        }

        description = string.Empty;
        return false;
    }

    private static bool TryFind(
        (string Name, string Description, bool TakesArguments)[] definitions,
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
                    description = value;
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

        foreach (var (name, description) in DatePartDefinitions)
        {
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
        (string Name, string Description, bool TakesArguments)[] definitions,
        SuggestionKind kind)
    {
        var suggestions = new List<SqlSuggestion>(definitions.Length);

        foreach (var (name, description, takesArguments) in definitions)
        {
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
