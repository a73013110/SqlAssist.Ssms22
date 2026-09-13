using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Sqlite;

public sealed partial class SqliteQueryMemoryRepository
{
    public Task<string[]> ReadConnectionFacetsAsync(QueryConnectionFacetRequest request, CancellationToken token) => Task.Run(() =>
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        using var connection = Connect();
        var column = request.Databases ? "h.DatabaseName" : "h.Server";
        var source = request.Saved ? "SavedQueries h JOIN Revisions r ON r.RevisionId=h.CurrentRevisionId" : "History h";
        var time = request.Saved ? "r.CreatedAt" : "h.CreatedAt";
        var order = request.Sort switch
        {
            QueryConnectionSort.Oldest => "MIN(" + time + ") ASC, Name ASC",
            QueryConnectionSort.Alphabetical => "Name COLLATE NOCASE ASC, Name ASC",
            QueryConnectionSort.ReverseAlphabetical => "Name COLLATE NOCASE DESC, Name DESC",
            _ => "MAX(" + time + ") DESC, Name ASC"
        };
        // 識別字與排序僅來自上述封閉集合；所有使用者值仍以參數傳入。
        using var command = Command(connection, null, "SELECT " + column + " AS Name FROM " + source +
            " WHERE " + column + " IS NOT NULL AND " + column + " <> ''" +
            (request.Saved ? " AND h.Scope=$scope" : "") +
            (request.Databases && request.Server != null ? " AND h.Server=$server" : "") +
            " GROUP BY " + column + " ORDER BY " + order + " LIMIT 101 OFFSET $offset;",
            ("$scope", (int)request.Scope), ("$server", request.Server), ("$offset", request.Offset));
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) { token.ThrowIfCancellationRequested(); names.Add(reader.GetString(0)); }
        return names.ToArray();
    }, token);
}
