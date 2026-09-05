using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 指令碼自己宣告的資料表：暫存資料表與資料表變數。
/// </summary>
/// <remarks>
/// 這兩種的欄位中繼資料一個都看不到——資料表變數根本不是 <c>sys.objects</c> 裡的
/// 物件，暫存資料表在 tempdb 裡，而擴充只查目前連線的那一個資料庫。症狀是
/// <c>UPDATE #tmp SET |</c> 與 <c>WHERE |</c> 完全沒有欄位建議、<c>SELECT *</c>
/// 按 Tab 展不開、<c>INSERT INTO #tmp</c> 提交之後只補了一個名稱。
///
/// 但那些欄位就寫在使用者眼前的 <c>CREATE TABLE #tmp (…)</c> 與
/// <c>DECLARE @tmp TABLE (…)</c> 括號裡，讀得出來——與 CTE 是同一條推理，
/// 也走同一條路：解析成 <see cref="SqlColumnSource.FromNames"/>，
/// 於是欄位建議、限定字欄位與萬用字元展開三處一次到位。
/// </remarks>
public sealed class SqlScriptTable
{
    /// <param name="start">宣告的第一個字元位置（<c>CREATE</c> 或變數本身）。</param>
    /// <param name="end">資料行清單右括號之後的位置。</param>
    public SqlScriptTable(string name, IReadOnlyList<SqlScriptColumn> columns, int start, int end)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("資料表名稱不可為空。", nameof(name));
        }

        if (columns is null)
        {
            throw new ArgumentNullException(nameof(columns));
        }

        Name = name;
        Columns = columns;
        Start = start;
        End = end;

        var names = new string[columns.Count];

        for (var index = 0; index < columns.Count; index++)
        {
            names[index] = columns[index].Name;
        }

        ColumnNames = names;
    }

    /// <summary>宣告時寫的名稱，含開頭的井號或小老鼠。</summary>
    public string Name { get; }

    /// <summary>
    /// 這份宣告在原始文字裡的範圍。
    /// </summary>
    /// <remarks>
    /// 留著位置而不是只留讀出來的資料行，是為了讓結構預覽的指令碼分頁交出<b>原文</b>。
    /// 由資料行重組一份 <c>CREATE TABLE</c> 會失真：讀得出「有沒有 DEFAULT」卻讀不出
    /// 它寫的是什麼，重組出來的那一段貼回編輯器根本執行不了。
    /// </remarks>
    public int Start { get; }

    public int End { get; }

    /// <summary>依宣告順序排列的資料行。</summary>
    public IReadOnlyList<SqlScriptColumn> Columns { get; }

    /// <summary>只要名稱的那一份；欄位建議與萬用字元展開用的就是它。</summary>
    public IReadOnlyList<string> ColumnNames { get; }

    public override string ToString() => $"{Name}（{Columns.Count} 個資料行）";
}
