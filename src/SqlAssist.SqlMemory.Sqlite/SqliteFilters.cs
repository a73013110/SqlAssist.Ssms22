using System;
using System.Collections.Generic;
using System.Threading;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>字面、區分大小寫的 KMP 搜尋；每次查詢只編譯一次，不為每列建立完整 SQL 字串。</summary>
internal sealed class SqliteTextMatcher
{
    private readonly string _needle;
    private readonly int[] _prefix;

    public SqliteTextMatcher(string needle)
    {
        _needle = needle;
        _prefix = new int[needle.Length];
        for (int i = 1, j = 0; i < needle.Length; i++)
        {
            while (j > 0 && needle[i] != needle[j]) j = _prefix[j - 1];
            if (needle[i] == needle[j]) j++;
            _prefix[i] = j;
        }
    }

    public bool Matches(byte[] bytes)
    {
        if (_needle.Length == 0) return true;
        for (int i = 0, j = 0; i + 1 < bytes.Length; i += 2)
        {
            var value = (char)(bytes[i] | (bytes[i + 1] << 8));
            while (j > 0 && value != _needle[j]) j = _prefix[j - 1];
            if (value == _needle[j]) j++;
            if (j == _needle.Length) return true;
        }
        return false;
    }
}

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
    private readonly SqliteTextMatcher _matcher;
    private readonly SqliteSearchBudget _budget;
    private readonly CancellationToken _cancellationToken;
    private int _candidates;
    private long _bytes;

    private SqliteSearchScan(string term, SqliteSearchBudget budget, CancellationToken cancellationToken)
    {
        Term = term;
        _matcher = new SqliteTextMatcher(term);
        _budget = budget;
        _cancellationToken = cancellationToken;
    }

    public static SqliteSearchScan? Create(string? search, SqliteSearchBudget budget, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(search) ? null : new SqliteSearchScan(search!, budget, cancellationToken);

    public string Term { get; }

    /// <summary>候選查詢的 LIMIT：多一列用來分辨「預算用盡但還有候選」與「剛好掃完」。</summary>
    public int CandidateLimit => _budget.Candidates + 1;

    public bool IsExhausted => _candidates >= _budget.Candidates || _bytes >= _budget.Bytes;

    /// <summary>
    /// 文字欄位在 C# 以 ordinal 比對，與 BLOB 的 UTF-16 code unit 語意一致；
    /// SQLite 的 instr 會先把參數轉 UTF-8，未配對 surrogate 會變成 U+FFFD 而誤判命中。
    /// </summary>
    public bool Matches(byte[] sql, params string?[] texts)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _candidates++;
        // SQLite 產生結果列時已讀入整份 BLOB，就算文字先命中也算進位元組預算。
        _bytes += sql.Length;
        foreach (var text in texts)
            if (text != null && text.IndexOf(Term, StringComparison.Ordinal) >= 0) return true;
        return _matcher.Matches(sql);
    }
}

/// <summary>History 與 Favorites 共用的伺服器／資料庫條件；兩張表同名欄位，時間索引也是同一組形狀。</summary>
internal static class SqliteConnectionFilter
{
    /// <param name="alias">資料表別名；只來自呼叫端常數。</param>
    public static void Append(ICollection<string> conditions, ICollection<(string Name, object? Value)> parameters,
        string alias, string? server, string? database)
    {
        if (server != null) { conditions.Add(alias + ".Server=$server"); parameters.Add(("$server", server)); }
        if (database != null) { conditions.Add(alias + ".DatabaseName=$database"); parameters.Add(("$database", database)); }
    }

    public static string Where(IReadOnlyCollection<string> conditions) =>
        conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
}
