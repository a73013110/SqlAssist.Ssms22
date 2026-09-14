using System;
using System.IO;
using Xunit;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

public sealed class SqliteRuntimeTests
{
    [Fact]
    public void NativeRuntimeLoadsInNet48X64AndReleasesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "SqlAssist.Sqlite." + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            Assert.True(Environment.Is64BitProcess);
            Assert.Equal("3.53.4", SqliteRuntime.Probe(path));
            using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }
}
