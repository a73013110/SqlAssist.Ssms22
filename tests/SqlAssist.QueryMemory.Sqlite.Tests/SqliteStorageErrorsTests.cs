using System;
using System.IO;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.QueryMemory.Sqlite.Tests;

public sealed class SqliteStorageErrorsTests
{
    [Theory]
    [InlineData(5, QueryMemoryStorageErrorKind.Busy)]
    [InlineData(6, QueryMemoryStorageErrorKind.Busy)]
    [InlineData(517, QueryMemoryStorageErrorKind.Busy)] // SQLITE_BUSY_SNAPSHOT
    [InlineData(11, QueryMemoryStorageErrorKind.Corrupt)]
    [InlineData(26, QueryMemoryStorageErrorKind.Corrupt)]
    [InlineData(13, QueryMemoryStorageErrorKind.Io)]
    [InlineData(19, QueryMemoryStorageErrorKind.Constraint)]
    [InlineData(1, QueryMemoryStorageErrorKind.Unknown)]
    public void SqliteCodesMapToKindsAndKeepTheOriginalCode(int code, QueryMemoryStorageErrorKind kind)
    {
        var error = SqliteStorageErrors.Translate(new SqliteException("測試", code, code));
        Assert.Equal(kind, error.Kind);
        Assert.Equal(code, error.ErrorCode);
        Assert.Equal(kind == QueryMemoryStorageErrorKind.Busy, error.IsTransient);
    }

    [Fact]
    public void RepositoryExceptionsAreClassifiedWithoutCarryingInnerExceptions()
    {
        Assert.Equal(QueryMemoryStorageErrorKind.Corrupt, SqliteStorageErrors.Translate(new InvalidDataException("雜湊不符")).Kind);
        Assert.Equal(QueryMemoryStorageErrorKind.InvalidArgument, SqliteStorageErrors.Translate(new ArgumentNullException("write")).Kind);
        Assert.Equal(QueryMemoryStorageErrorKind.Unknown, SqliteStorageErrors.Translate(new InvalidOperationException("未知")).Kind);
        var cursor = new QueryMemoryStorageException(QueryMemoryStorageErrorKind.InvalidCursor, "游標失效");
        var translated = SqliteStorageErrors.Translate(new AggregateException(cursor));
        Assert.Equal(QueryMemoryStorageErrorKind.InvalidCursor, translated.Kind);
        Assert.Equal("游標失效", translated.Message);
        Assert.Null(translated.InnerException);
    }
}
