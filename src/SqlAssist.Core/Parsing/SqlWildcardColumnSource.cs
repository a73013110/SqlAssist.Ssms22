using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 展開萬用字元時，欄位的其中一個來源。
/// </summary>
/// <remarks>
/// 一個 <c>*</c> 可能同時來自好幾個地方——<c>FROM A a JOIN B b</c> 是兩個，
/// <c>(SELECT Id, * FROM T) d</c> 也是兩個（一個寫死的名稱、一個資料表）。
/// 因此展開的結果是一串來源而不是一張資料表，串接的順序就是欄位的輸出順序。
///
/// <see cref="Qualifier"/> 一律是<b>最外層</b>那個名稱：<c>(SELECT * FROM T t) d</c>
/// 的欄位在外層要寫成 <c>d.欄位</c>，內層的 <c>t</c> 在外面根本不存在。
/// </remarks>
public sealed class SqlWildcardColumnSource
{
    private static readonly IReadOnlyList<string> NoNames = Array.Empty<string>();

    private SqlWildcardColumnSource(
        SqlWildcardSourceKind kind,
        SqlTableReference? table,
        IReadOnlyList<string> names,
        string? qualifier)
    {
        Kind = kind;
        Table = table;
        Names = names;
        Qualifier = qualifier;
    }

    public SqlWildcardSourceKind Kind { get; }

    /// <summary>要查詢欄位的資料來源；<see cref="Kind"/> 為 <see cref="SqlWildcardSourceKind.Table"/> 時不為 null。</summary>
    public SqlTableReference? Table { get; }

    /// <summary>已知的欄位名稱；<see cref="Kind"/> 為 <see cref="SqlWildcardSourceKind.Names"/> 時才有內容。</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>展開後要補在欄位前面的名稱；敘述裡讀不出可用的名稱時為 null。</summary>
    public string? Qualifier { get; }

    public static SqlWildcardColumnSource FromTable(SqlTableReference table, string? qualifier)
    {
        if (table is null)
        {
            throw new ArgumentNullException(nameof(table));
        }

        return new SqlWildcardColumnSource(SqlWildcardSourceKind.Table, table, NoNames, qualifier);
    }

    public static SqlWildcardColumnSource FromNames(IReadOnlyList<string> names, string? qualifier)
    {
        if (names is null)
        {
            throw new ArgumentNullException(nameof(names));
        }

        return new SqlWildcardColumnSource(SqlWildcardSourceKind.Names, table: null, names, qualifier);
    }
}
