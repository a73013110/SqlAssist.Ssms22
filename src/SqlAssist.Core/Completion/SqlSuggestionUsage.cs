using System;
using System.Collections.Generic;
using System.Threading;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 最近提交過的建議項。
/// </summary>
/// <remarks>
/// 排名裡唯一「會學習」的部分：剛用過 <c>Lib_Reader_Tag</c> 的人，下一次輸入
/// <c>sys</c> 時要的多半還是它，而不是字母序剛好排在前面的那一個。
///
/// 只活在行程記憶體裡，關掉 SSMS 就歸零。刻意不落地：這份資料的價值集中在
/// 「這一段工作期間」，為它多一個檔案、一組失效規則與一次磁碟 I/O 並不划算。
///
/// 讀取落在每一次按鍵、每一個候選項上，因此讀的那一份集合是唯讀的：
/// 寫入時整份換新（複製後替換參考），讀取端完全不必上鎖。
/// </remarks>
public static class SqlSuggestionUsage
{
    /// <summary>記住幾筆。</summary>
    /// <remarks>
    /// 夠一段工作期間內反覆用到的那些物件，又不至於多到「什麼都算最近用過」——
    /// 加成一旦人人有份就等於沒有。
    /// </remarks>
    public const int Capacity = 32;

    private static readonly object Gate = new();

    /// <summary>最近使用的順序，最新的在最前面。只在 <see cref="Gate"/> 之下存取。</summary>
    private static readonly List<string> Order = new(Capacity);

    private static HashSet<string> _recent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>記下一次提交。</summary>
    public static void Record(SqlSuggestion? suggestion)
    {
        if (suggestion is null || string.IsNullOrWhiteSpace(suggestion.DisplayText))
        {
            return;
        }

        var key = KeyOf(suggestion);

        lock (Gate)
        {
            Order.RemoveAll(item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase));
            Order.Insert(0, key);

            if (Order.Count > Capacity)
            {
                Order.RemoveRange(Capacity, Order.Count - Capacity);
            }

            Volatile.Write(ref _recent, new HashSet<string>(Order, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>這一項最近提交過嗎。</summary>
    public static bool IsRecent(SqlSuggestion? suggestion)
    {
        return suggestion is not null &&
               !string.IsNullOrEmpty(suggestion.DisplayText) &&
               Volatile.Read(ref _recent).Contains(KeyOf(suggestion));
    }

    /// <summary>
    /// 類別與名稱一起當鍵。
    /// </summary>
    /// <remarks>
    /// 只用名稱的話，同名的欄位與資料表會被當成同一件事——提交過
    /// 資料表 <c>PUBLCODE</c>，同名的欄位也跟著拿到加成，而那是兩個不同的東西。
    /// </remarks>
    private static string KeyOf(SqlSuggestion suggestion)
    {
        return ((int)suggestion.Kind).ToString() + ':' + suggestion.DisplayText;
    }

    /// <summary>清空；測試用。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Order.Clear();
            Volatile.Write(ref _recent, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }
}
