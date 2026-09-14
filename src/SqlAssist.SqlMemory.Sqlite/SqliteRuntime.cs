using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SqlAssist.SqlMemory.Sqlite;

/// <summary>SQLite provider 邊界；核心與 SSMS 接線層都不需要知道 native API。</summary>
public static class SqliteRuntime
{
    public static string Probe(string databasePath)
    {
        // 只查 runtime 版本，不寫入使用者 SQL。
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Pooling = false,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("SQLite runtime 沒有回傳版本。");
    }
}
