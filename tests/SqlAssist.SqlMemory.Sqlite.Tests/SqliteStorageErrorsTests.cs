using System;
using System.IO;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

public sealed class SqliteStorageErrorsTests
{
    [Theory]
    [InlineData(5, SqlMemoryStorageErrorKind.Busy)]
    [InlineData(6, SqlMemoryStorageErrorKind.Busy)]
    [InlineData(517, SqlMemoryStorageErrorKind.Busy)] // SQLITE_BUSY_SNAPSHOT
    [InlineData(11, SqlMemoryStorageErrorKind.Corrupt)]
    [InlineData(26, SqlMemoryStorageErrorKind.Corrupt)]
    [InlineData(13, SqlMemoryStorageErrorKind.Io)]
    [InlineData(19, SqlMemoryStorageErrorKind.Constraint)]
    [InlineData(1, SqlMemoryStorageErrorKind.Unknown)]
    public void SqliteCodesMapToKindsAndKeepTheOriginalCode(int code, SqlMemoryStorageErrorKind kind)
    {
        var error = SqliteStorageErrors.Translate(new SqliteException("測試", code, code));
        Assert.Equal(kind, error.Kind);
        Assert.Equal(code, error.ErrorCode);
        Assert.Equal(kind == SqlMemoryStorageErrorKind.Busy, error.IsTransient);
    }

    [Fact]
    public void RepositoryExceptionsAreClassifiedWithoutCarryingInnerExceptions()
    {
        Assert.Equal(SqlMemoryStorageErrorKind.Corrupt, SqliteStorageErrors.Translate(new InvalidDataException("雜湊不符")).Kind);
        Assert.Equal(SqlMemoryStorageErrorKind.InvalidArgument, SqliteStorageErrors.Translate(new ArgumentNullException("write")).Kind);
        Assert.Equal(SqlMemoryStorageErrorKind.Unknown, SqliteStorageErrors.Translate(new InvalidOperationException("未知")).Kind);
        var cursor = new SqlMemoryStorageException(SqlMemoryStorageErrorKind.InvalidCursor, "游標失效");
        var translated = SqliteStorageErrors.Translate(new AggregateException(cursor));
        Assert.Equal(SqlMemoryStorageErrorKind.InvalidCursor, translated.Kind);
        Assert.Equal("游標失效", translated.Message);
        Assert.Null(translated.InnerException);
    }
}
