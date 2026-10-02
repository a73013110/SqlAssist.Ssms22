using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

/// <summary>游標所在查詢範圍內的資料來源，連同包住它的外層查詢。</summary>
public sealed class SqlStatementScope
{
    public static readonly SqlStatementScope Empty =
        new(Array.Empty<SqlTableReference>(), 0, 0);

    public SqlStatementScope(
        IReadOnlyList<SqlTableReference> tables,
        int start,
        int end,
        SqlStatementScope? outer = null,
        IReadOnlyList<SqlTableReference>? changeTables = null,
        SqlTableReference? triggerTable = null,
        IReadOnlyList<SqlTableReference>? lateral = null)
    {
        Tables = tables;
        Start = start;
        End = end;
        Outer = outer;
        ChangeTables = changeTables ?? Array.Empty<SqlTableReference>();
        TriggerTable = triggerTable;
        Lateral = lateral ?? Array.Empty<SqlTableReference>();
        ColumnTables = Lateral.Count == 0 ? tables : Concat(tables, Lateral);
    }

    /// <summary>此範圍內的資料來源，依出現順序排列。</summary>
    /// <remarks>
    /// 只有這一層自己的 FROM 子句：未限定的欄位與 <c>SELECT *</c> 展開都只屬於這一層，
    /// 外層的來源由 <see cref="Outer"/> 一層一層往外接。
    /// </remarks>
    public IReadOnlyList<SqlTableReference> Tables { get; }

    /// <summary>
    /// 這一層是 APPLY 右邊的衍生資料表時，APPLY 左邊的來源；其餘是空的。
    /// </summary>
    /// <remarks>
    /// 子查詢切開的是未限定的那一半，APPLY 右邊例外：它就是為了逐列引用左邊而寫的，沒寫限定字的欄位
    /// 也引用得到左邊（<c>FROM (VALUES (1)) d (Seq) CROSS APPLY (SELECT CAST(Seq AS bigint) AS Big) x</c>）。
    /// 不併進 <see cref="Tables"/>：右邊查詢的 <c>SELECT *</c> 只展開它自己的 FROM。
    /// </remarks>
    public IReadOnlyList<SqlTableReference> Lateral { get; }

    /// <summary>沒寫限定字的欄位可能屬於的來源：<see cref="Tables"/> 在前，<see cref="Lateral"/> 在後。</summary>
    public IReadOnlyList<SqlTableReference> ColumnTables { get; }

    /// <summary>範圍在原始文字中的起訖位置。</summary>
    public int Start { get; }

    public int End { get; }

    /// <summary>包住這一層的查詢；這一層不在子查詢的括號裡時是 null。</summary>
    /// <remarks>
    /// 相互關聯子查詢（<c>NOT EXISTS (SELECT … WHERE c.x = a.|)</c>）的 <c>a</c> 屬於外層。
    /// 只看這一層的話，<c>a.</c> 會退回「a 是結構描述」的解讀而一個欄位都列不出來，
    /// 滑鼠停留與 F12 也找不到那張表。
    /// </remarks>
    public SqlStatementScope? Outer { get; }

    /// <summary>
    /// 游標那一格引用得到的 <c>inserted</c>／<c>deleted</c>，別名就是那兩個名字：OUTPUT 子句裡指那句 DML 的目標，
    /// DML 觸發程序的主體裡指父資料表；其餘位置是空的。
    /// </summary>
    /// <remarks>
    /// 觸發程序裡它們要寫在 FROM 才算數，但選取清單寫在 FROM 之前：<c>SELECT inserted.| FROM inserted</c>
    /// 還沒寫到 FROM 時也要列得出來。
    ///
    /// 不併進 <see cref="Tables"/>：那一份是「沒寫限定字的欄位屬於誰」，OUTPUT 的欄位一定要寫
    /// <c>inserted.</c>，併進去的話 <c>OUTPUT </c> 之後同一份欄位列兩次。
    /// </remarks>
    public IReadOnlyList<SqlTableReference> ChangeTables { get; }

    /// <summary>游標在 DML 觸發程序的主體裡時，觸發程序的父資料表；FROM 之後列得出 inserted、deleted。</summary>
    public SqlTableReference? TriggerTable { get; }

    /// <summary>
    /// 把限定字解析成資料來源。
    /// </summary>
    /// <remarks>
    /// 別名優先於物件名稱：<c>FROM Loans AS Publishers</c> 之後的 <c>Publishers.</c>
    /// 指的是 Loans，不是另一張同名資料表。
    ///
    /// 由內往外找，內層先找到就停：這正是 T-SQL 解析相關名稱的順序，
    /// 子查詢裡與外層同名的別名遮住外層那一個。
    /// </remarks>
    public bool TryResolve(string qualifier, out SqlTableReference reference)
    {
        reference = null!;

        if (string.IsNullOrEmpty(qualifier))
        {
            return false;
        }

        // OUTPUT 子句裡的 inserted 一定是那句 DML 的，同一句 FROM 寫的 inserted 遮不住它。
        foreach (var candidate in ChangeTables)
        {
            if (string.Equals(candidate.Alias, qualifier, StringComparison.OrdinalIgnoreCase))
            {
                reference = candidate;
                return true;
            }
        }

        for (var scope = this; scope is not null; scope = scope.Outer)
        {
            if (scope.TryResolveHere(qualifier, out reference))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryResolveHere(string qualifier, out SqlTableReference reference)
    {
        foreach (var candidate in Tables)
        {
            if (!string.IsNullOrEmpty(candidate.Alias) &&
                string.Equals(candidate.Alias, qualifier, StringComparison.OrdinalIgnoreCase))
            {
                reference = candidate;
                return true;
            }
        }

        foreach (var candidate in Tables)
        {
            if (string.IsNullOrEmpty(candidate.Alias) &&
                string.Equals(candidate.ObjectName, qualifier, StringComparison.OrdinalIgnoreCase))
            {
                reference = candidate;
                return true;
            }
        }

        reference = null!;
        return false;
    }

    private static IReadOnlyList<SqlTableReference> Concat(
        IReadOnlyList<SqlTableReference> first,
        IReadOnlyList<SqlTableReference> second)
    {
        var all = new List<SqlTableReference>(first.Count + second.Count);
        all.AddRange(first);
        all.AddRange(second);
        return all;
    }
}
