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
///
/// <c>SELECT … INTO #tmp</c> 也是這個型別，只是資料行由那句 <c>SELECT</c> 的選取
/// 清單投影出來、而且是<b>延後</b>算的，型別一律讀不出來。用同一個型別而不是另立
/// 一個，是為了讓下游（圖示、提交後的整句展開、滑鼠停留、結構預覽）一個字都不必
/// 分辨——它們問的都是同一件事：這個名稱有哪些資料行。
/// </remarks>
public sealed class SqlScriptTable
{
    /// <summary>資料行還沒算出來時留著的那一份；算完就丟。</summary>
    private Func<IReadOnlyList<SqlScriptColumn>>? _resolveColumns;

    private IReadOnlyList<SqlScriptColumn>? _columns;

    private IReadOnlyList<string>? _columnNames;

    /// <param name="start">宣告的第一個字元位置（<c>CREATE</c> 或變數本身）。</param>
    /// <param name="end">資料行清單右括號之後的位置。</param>
    public SqlScriptTable(string name, IReadOnlyList<SqlScriptColumn> columns, int start, int end)
        : this(name, start, end)
    {
        _columns = columns ?? throw new ArgumentNullException(nameof(columns));
    }

    /// <summary>
    /// 資料行等到真的有人要看才算出來。
    /// </summary>
    /// <remarks>
    /// <c>SELECT … INTO #tmp</c> 走這一支：它的資料行要把那句 <c>SELECT</c> 的選取
    /// 清單整段攤平（內層還可能是子查詢或另一張暫存資料表），而建議清單在
    /// <c>FROM</c> 之後只需要<b>名稱</b>——那條路徑在每一次按鍵上，
    /// 每個名稱都先攤一次是白付的。真正要資料行的只有提交之後的整句展開、
    /// 滑鼠停留提示與結構預覽，三者都是使用者做了一個動作才發生。
    ///
    /// 算完就把委派丟掉：它抓著整份詞法單元，而建議項會活到這一輪補全結束。
    /// </remarks>
    /// <param name="resolveColumns">算出資料行的那一份；只會被叫到一次。</param>
    public SqlScriptTable(
        string name,
        Func<IReadOnlyList<SqlScriptColumn>> resolveColumns,
        int start,
        int end)
        : this(name, start, end)
    {
        _resolveColumns = resolveColumns ?? throw new ArgumentNullException(nameof(resolveColumns));
    }

    private SqlScriptTable(string name, int start, int end)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("資料表名稱不可為空。", nameof(name));
        }

        Name = name;
        Start = start;
        End = end;
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
    public IReadOnlyList<SqlScriptColumn> Columns
    {
        get
        {
            if (_columns is null)
            {
                _columns = _resolveColumns!();
                _resolveColumns = null;
            }

            return _columns;
        }
    }

    /// <summary>只要名稱的那一份；欄位建議與萬用字元展開用的就是它。</summary>
    public IReadOnlyList<string> ColumnNames => _columnNames ??= BuildColumnNames();

    public override string ToString() => $"{Name}（{Columns.Count} 個資料行）";

    private IReadOnlyList<string> BuildColumnNames()
    {
        var columns = Columns;
        var names = new string[columns.Count];

        for (var index = 0; index < columns.Count; index++)
        {
            names[index] = columns[index].Name;
        }

        return names;
    }
}
