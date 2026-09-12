using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace SqlAssist.QueryMemory.Sqlite;

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

/// <summary>History 與 Saved 共用同一個字面搜尋條件，語意不隨呼叫端分岔。</summary>
internal sealed class SqliteSearchFilter
{
    private const string Function = "qm_matches";
    private readonly SqliteTextMatcher _matcher;
    private readonly string _term;

    private SqliteSearchFilter(string term)
    {
        _term = term;
        _matcher = new SqliteTextMatcher(term);
    }

    public static SqliteSearchFilter? Create(string? search) =>
        string.IsNullOrEmpty(search) ? null : new SqliteSearchFilter(search!);

    /// <summary>
    /// 已索引的 TEXT 欄位排在 BLOB 掃描之前，靠 OR 短路擋掉多數候選，不必每列解出完整 SQL。
    /// TEXT 走 SQLite 的 UTF-8 位元組比對、BLOB 走 UTF-16 code unit 比對；兩者對有效文字結果一致。
    /// </summary>
    public string Apply(SqliteConnection connection, List<(string, object?)> parameters, string blobColumn,
        CancellationToken cancellationToken, params string[] textColumns)
    {
        connection.CreateFunction<byte[], bool>(Function, bytes =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _matcher.Matches(bytes);
        });
        var condition = new StringBuilder("(");
        if (textColumns.Length != 0)
        {
            parameters.Add(("$search", _term));
            foreach (var column in textColumns) condition.Append("instr(").Append(column).Append(",$search)>0 OR ");
        }
        return condition.Append(Function).Append('(').Append(blobColumn).Append("))").ToString();
    }
}

/// <summary>游標指紋共用的欄位編碼；長度前綴避免分隔符出現在使用者文字時，讓不同 filter 得到同一指紋。</summary>
internal static class SqliteFilterKey
{
    public static string Field(string? value) =>
        value == null ? "-1:" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
}
