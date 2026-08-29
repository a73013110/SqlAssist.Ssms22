using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 一個資料來源攤平之後的欄位出處。
/// </summary>
/// <remarks>
/// 一個資料來源可能同時來自好幾個地方——<c>FROM A a JOIN B b</c> 是兩個，
/// <c>(SELECT Id, * FROM T) d</c> 也是兩個（一個寫死的名稱、一個資料表）。
/// 因此攤平的結果是一串來源而不是一張資料表，串接的順序就是欄位的輸出順序。
///
/// <see cref="Qualifier"/> 一律是<b>最外層</b>那個名稱：<c>(SELECT * FROM T t) d</c>
/// 的欄位在外層要寫成 <c>d.欄位</c>，內層的 <c>t</c> 在外面根本不存在。
/// </remarks>
public sealed class SqlColumnSource
{
    private static readonly IReadOnlyList<string> NoNames = Array.Empty<string>();

    private SqlColumnSource(
        SqlColumnSourceKind kind,
        SqlTableReference? table,
        IReadOnlyList<string> names,
        string? qualifier)
    {
        Kind = kind;
        Table = table;
        Names = names;
        Qualifier = qualifier;
    }

    public SqlColumnSourceKind Kind { get; }

    /// <summary>要查詢欄位的資料來源；<see cref="Kind"/> 為 <see cref="SqlColumnSourceKind.Table"/> 時不為 null。</summary>
    public SqlTableReference? Table { get; }

    /// <summary>已知的欄位名稱；<see cref="Kind"/> 為 <see cref="SqlColumnSourceKind.Names"/> 時才有內容。</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>要補在欄位前面的名稱；敘述裡讀不出可用的名稱時為 null。</summary>
    public string? Qualifier { get; }

    public static SqlColumnSource FromTable(SqlTableReference table, string? qualifier)
    {
        if (table is null)
        {
            throw new ArgumentNullException(nameof(table));
        }

        return new SqlColumnSource(SqlColumnSourceKind.Table, table, NoNames, qualifier);
    }

    public static SqlColumnSource FromNames(IReadOnlyList<string> names, string? qualifier)
    {
        if (names is null)
        {
            throw new ArgumentNullException(nameof(names));
        }

        return new SqlColumnSource(SqlColumnSourceKind.Names, table: null, names, qualifier);
    }
}
