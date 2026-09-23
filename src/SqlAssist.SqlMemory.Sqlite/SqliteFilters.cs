using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using SqlAssist.Core.Matching;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>單頁搜尋最多檢查的候選列數與 SQL BLOB 位元組數；兩者先到者為準。</summary>
internal sealed class SqliteSearchBudget
{
    public static readonly SqliteSearchBudget Default = new(2000, 16L * 1024 * 1024);

    public SqliteSearchBudget(int candidates, long bytes)
    {
        // 候選查詢多讀一列判斷是否還有下一頁，上限因此留一格給 +1。
        if (candidates < 1 || candidates == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(candidates));
        if (bytes < 1) throw new ArgumentOutOfRangeException(nameof(bytes));
        Candidates = candidates;
        Bytes = bytes;
    }

    public int Candidates { get; }
    public long Bytes { get; }
}

/// <summary>
/// History 與 Favorite 共用的字面搜尋；比對在讀取迴圈內逐列進行，而不是當成 SQL 的 WHERE 條件。
/// </summary>
/// <remarks>
/// 改成註冊自訂 scalar function 放進 WHERE 的話，SQLite 會一路掃到湊滿 LIMIT 或掃完整表，
/// 命中很少的搜尋沒有延遲上界，呼叫端也拿不到「掃到哪裡」來續頁。由讀取端計數後，
/// 預算用盡就以最後檢查過的鍵產生游標；成本相同（UDF 本來也要把 BLOB 複製成 byte[]）。
/// 候選查詢必須沿索引串流、不能有暫存排序，否則第一列出來前就已讀完所有 BLOB，預算形同虛設。
/// </remarks>
internal sealed class SqliteSearchScan
{
    private readonly TextMatcher _matcher;
    private readonly SqliteSearchBudget _budget;
    private readonly CancellationToken _cancellationToken;
    private int _candidates;
    private long _bytes;

    private SqliteSearchScan(TextMatcher matcher, SqliteSearchBudget budget, CancellationToken cancellationToken)
    {
        _matcher = matcher;
        _budget = budget;
        _cancellationToken = cancellationToken;
    }

    public static SqliteSearchScan? Create(string? search, TextMatchOptions options, SqliteSearchBudget budget,
        CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(search)
            ? null
            : new SqliteSearchScan(new TextMatcher(search!, options), budget, cancellationToken);

    /// <summary>候選查詢的 LIMIT：多一列用來分辨「預算用盡但還有候選」與「剛好掃完」。</summary>
    public int CandidateLimit => _budget.Candidates + 1;

    public bool IsExhausted => _candidates >= _budget.Candidates || _bytes >= _budget.Bytes;

    /// <summary>
    /// 文字欄位與 SQL BLOB 走同一個 <see cref="TextMatcher"/>，都是 UTF-16 code unit 語意；
    /// 不交給 SQLite 的 instr：它會先把參數轉 UTF-8，未配對 surrogate 會變成 U+FFFD 而誤判命中。
    /// </summary>
    public bool Matches(byte[] sql, params string?[] texts)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _candidates++;
        // SQLite 產生結果列時已讀入整份 BLOB，就算文字先命中也算進位元組預算。
        _bytes += sql.Length;
        foreach (var text in texts)
            if (_matcher.IsMatch(text)) return true;
        return _matcher.IsMatchUtf16(sql);
    }
}

/// <summary>History 與 Favorites 共用的伺服器／資料庫條件；兩張表同名欄位，時間索引也是同一組形狀。</summary>
internal static class SqliteConnectionFilter
{
    /// <param name="alias">資料表別名；只來自呼叫端常數。</param>
    public static void Append(ICollection<string> conditions, ICollection<(string Name, object? Value)> parameters,
        string alias, string? server, string? database)
    {
        Append(conditions, parameters, alias, Names(server), Names(database));
    }

    /// <param name="alias">資料表別名；只來自呼叫端常數。</param>
    /// <param name="servers">空名單表示不限。</param>
    /// <param name="databases">空名單表示不限。</param>
    public static void Append(ICollection<string> conditions, ICollection<(string Name, object? Value)> parameters,
        string alias, IReadOnlyList<string> servers, IReadOnlyList<string> databases)
    {
        AppendColumn(conditions, parameters, alias + ".Server", "$server", servers);
        AppendColumn(conditions, parameters, alias + ".DatabaseName", "$database", databases);
    }

    /// <summary>
    /// 一個欄位的條件：一個名稱用 <c>=</c>，多個用 <c>IN</c>。
    /// </summary>
    /// <remarks>
    /// 多值走 <c>IN</c> 而不是把索引封死：SQLite 仍然可以為它挑
    /// <c>IX_History_ServerTime</c> 這類索引，但那時 <c>ORDER BY</c> 落在索引後段的欄位上，
    /// 它會為排序建一棵暫存 b-tree——而整份分頁的前提正是沿時間索引串流，
    /// 搜尋預算才真的限制得了讀進來的 BLOB。所以多值那一支在名稱前加上一元 <c>+</c>，
    /// 讓這個條件不能當成索引限制，查詢回到時間索引上邊走邊濾；
    /// 只勾一個的常見情形仍然吃得到複合索引。EXPLAIN 測試同時守住這兩種形狀。
    /// </remarks>
    private static void AppendColumn(ICollection<string> conditions,
        ICollection<(string Name, object? Value)> parameters, string column, string prefix, IReadOnlyList<string> names)
    {
        if (names is null) throw new ArgumentNullException(nameof(names));
        if (names.Count == 0) return;

        if (names.Count == 1)
        {
            conditions.Add(column + "=" + prefix);
            parameters.Add((prefix, names[0]));
            return;
        }

        var placeholders = new string[names.Count];
        for (var index = 0; index < names.Count; index++)
        {
            placeholders[index] = prefix + index.ToString(CultureInfo.InvariantCulture);
            parameters.Add((placeholders[index], names[index]));
        }

        conditions.Add("+" + column + " IN (" + string.Join(",", placeholders) + ")");
    }

    private static IReadOnlyList<string> Names(string? value) =>
        value is null ? Array.Empty<string>() : new[] { value };

    public static string Where(IReadOnlyCollection<string> conditions) =>
        conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
}
