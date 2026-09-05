using System;
using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Metadata.Model;

/// <summary>可重用的物件識別字與敘述範圍；不保存任何中繼資料解析結果。</summary>
/// <remarks>
/// 同一份 SQL 的詞法分析只做一次，但每次停留都比對當下的快照及明細，
/// 避免清快取、背景載入或切換連線後繼續沿用舊物件或「查無物件」。
/// </remarks>
public sealed class SqlObjectLookup
{
    private readonly SqlStatementScope _scope;

    private SqlObjectLookup(SqlIdentifierReference reference, SqlStatementScope scope)
    {
        Reference = reference;
        _scope = scope;
    }

    public SqlIdentifierReference Reference { get; }

    public static SqlObjectLookup? Create(string text, int position)
    {
        var reference = SqlIdentifierScanner.FindAt(text, position);
        return reference is null ? null : new SqlObjectLookup(reference, SqlScopeAnalyzer.Analyze(text, position));
    }

    public sealed class Candidate
    {
        internal Candidate(SqlObjectInfo objectInfo, bool needsColumn)
        {
            Object = objectInfo;
            NeedsColumn = needsColumn;
        }

        public SqlObjectInfo Object { get; }
        public bool NeedsColumn { get; }
    }

    public Candidate? FindCandidate(SqlDatabaseSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.IsEmpty)
        {
            return null;
        }

        if (TryResolveColumnOwner(snapshot, _scope, Reference, out var owner))
        {
            return new Candidate(owner, needsColumn: true);
        }

        var matches = ResolveObject(snapshot, _scope, Reference);
        return matches.Count == 0 ? null : new Candidate(matches[0], needsColumn: false);
    }

    public SqlObjectLocation? Locate(Candidate candidate, SqlObjectDetail? detail = null)
    {
        return candidate.NeedsColumn
            ? BuildColumnLocation(Reference, candidate.Object, detail)
            : new SqlObjectLocation(Reference, candidate.Object);
    }

    /// <summary>判斷這個參考是不是「敘述中某個資料來源的欄位」，是的話取出該資料來源。</summary>
    private static bool TryResolveColumnOwner(
        SqlDatabaseSnapshot snapshot,
        SqlStatementScope scope,
        SqlIdentifierReference reference,
        out SqlObjectInfo owner)
    {
        owner = null!;

        // 多段的限定字不可能是別名：別名只有一段。多段時拿最右邊那一段去比對別名，
        // 剛好取名叫 dbo 的別名會讓 F12 跳到它指的那張表。
        if (reference.Qualifier is null ||
            reference.Path is not { IsLocal: true } ||
            !scope.TryResolve(reference.Qualifier, out var table) ||
            table.IsDerived)
        {
            return false;
        }

        var matches = snapshot.Find(table.ObjectName, table.SchemaName);

        if (matches.Count == 0)
        {
            return false;
        }

        owner = matches[0];
        return true;
    }

    private static SqlObjectLocation? BuildColumnLocation(
        SqlIdentifierReference reference,
        SqlObjectInfo owner,
        SqlObjectDetail? detail)
    {
        if (detail is null)
        {
            // 明細還沒回來時仍回報物件，呼叫端可以顯示載入中的內容。
            return new SqlObjectLocation(reference, owner);
        }

        foreach (var column in detail.Columns)
        {
            if (string.Equals(column.Name, reference.Name, StringComparison.OrdinalIgnoreCase))
            {
                return new SqlObjectLocation(reference, owner, column);
            }
        }

        // 限定詞確實是資料來源，只是沒有這個欄位；不要退回去猜同名的資料庫物件。
        return null;
    }

    /// <summary>
    /// 把識別字解析成資料庫物件。
    /// </summary>
    /// <remarks>
    /// 沒有限定詞的識別字可能是敘述裡的別名，這時要換成別名指向的資料表。
    /// 別名優先於同名物件：<c>FROM Loan AS PUBLISHER</c> 之後的 <c>PUBLISHER</c> 是 Loan。
    /// </remarks>
    private static IReadOnlyList<SqlObjectInfo> ResolveObject(
        SqlDatabaseSnapshot snapshot,
        SqlStatementScope scope,
        SqlIdentifierReference reference)
    {
        if (reference.Qualifier is null &&
            scope.TryResolve(reference.Name, out var aliased) &&
            !aliased.IsDerived)
        {
            // 別名可能指向另一個資料庫的表，那時這一份快照回答不了它。
            if (aliased.Path is { IsLocal: false })
            {
                return Array.Empty<SqlObjectInfo>();
            }

            var byAlias = snapshot.Find(aliased.ObjectName, aliased.SchemaName);

            if (byAlias.Count > 0)
            {
                return byAlias;
            }
        }

        var matches = snapshot.Find(reference.Name, reference.Qualifier);

        // 限定詞找不到時退回只用名稱比對——但只限這個名稱本來就指著這份快照的時候。
        // 跨資料庫或伺服器時猜測同名物件，會讓畫面看似正常卻指向錯誤的結構。
        return matches.Count == 0 && reference.Qualifier is not null && reference.IsLocal
            ? snapshot.Find(reference.Name)
            : matches;
    }
}
