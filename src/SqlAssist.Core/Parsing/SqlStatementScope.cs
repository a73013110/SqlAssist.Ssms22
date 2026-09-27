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
        SqlStatementScope? outer = null)
    {
        Tables = tables;
        Start = start;
        End = end;
        Outer = outer;
    }

    /// <summary>此範圍內的資料來源，依出現順序排列。</summary>
    /// <remarks>
    /// 只有這一層自己的 FROM 子句：未限定的欄位與 <c>SELECT *</c> 展開都只屬於這一層，
    /// 外層的來源由 <see cref="Outer"/> 一層一層往外接。
    /// </remarks>
    public IReadOnlyList<SqlTableReference> Tables { get; }

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
}
