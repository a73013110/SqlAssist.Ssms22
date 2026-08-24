using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

/// <summary>游標所在查詢範圍內的資料來源。</summary>
public sealed class SqlStatementScope
{
    public static readonly SqlStatementScope Empty =
        new(Array.Empty<SqlTableReference>(), 0, 0);

    public SqlStatementScope(IReadOnlyList<SqlTableReference> tables, int start, int end)
    {
        Tables = tables;
        Start = start;
        End = end;
    }

    /// <summary>此範圍內的資料來源，依出現順序排列。</summary>
    public IReadOnlyList<SqlTableReference> Tables { get; }

    /// <summary>範圍在原始文字中的起訖位置。</summary>
    public int Start { get; }

    public int End { get; }

    /// <summary>
    /// 把限定字解析成資料來源。
    /// </summary>
    /// <remarks>
    /// 別名優先於物件名稱：<c>FROM Orders AS Publishers</c> 之後的 <c>Publishers.</c>
    /// 指的是 Orders，不是另一張同名資料表。
    /// </remarks>
    public bool TryResolve(string qualifier, out SqlTableReference reference)
    {
        reference = null!;

        if (string.IsNullOrEmpty(qualifier))
        {
            return false;
        }

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

        return false;
    }
}
