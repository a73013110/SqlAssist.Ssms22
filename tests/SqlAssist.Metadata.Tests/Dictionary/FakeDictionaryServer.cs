using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Tests.Dictionary;

/// <summary>
/// 只替代資料庫 I/O 的一台伺服器：伺服器上現在的說明、交易有沒有提交、執行過哪幾段文字。
/// </summary>
/// <remarks>
/// 併發檢查那一條照查詢文字裡 <c>fn_listextendedproperty</c> 的引數找值——那正是產品寫進去的目標，
/// 認錯引數的話這裡就回不出值，檢查會在測試上失敗，而不是安靜地通過。
/// </remarks>
internal sealed class FakeDictionaryServer : ISqlConnectionSource
{
    private static readonly Regex ChangeLine = new(
        @"SELECT (?<index>\d+) AS change_index.*?fn_listextendedproperty\(N'MS_Description', 'SCHEMA', N'(?<schema>[^']*)', '(?<parent>\w+)', N'(?<object>[^']*)', (?:'COLUMN', N'(?<column>[^']*)'|NULL, NULL)\)");

    /// <summary>伺服器上現在的說明：鍵是 <c>schema.object</c> 或 <c>schema.object.column</c>。</summary>
    public Dictionary<string, string> Descriptions { get; } = new(StringComparer.Ordinal);

    public List<string> Executed { get; } = new();

    public bool Committed { get; private set; }

    public bool RolledBack { get; private set; }

    /// <summary>設了就在寫入那一步丟 <see cref="DbException"/>。</summary>
    public string? DenyWrite { get; set; }

    public string CacheKey => "LIBSQL01|Library";

    public string ServerCacheKey => "LIBSQL01";

    public string DatabaseName => "Library";

    public IDbConnection OpenConnection() => new Connection(this);

    private sealed class Connection : IDbConnection
    {
        private readonly FakeDictionaryServer _server;

        public Connection(FakeDictionaryServer server) => _server = server;

        [AllowNull] public string ConnectionString { get; set; } = string.Empty;

        public int ConnectionTimeout => 0;

        public string Database => _server.DatabaseName;

        public ConnectionState State => ConnectionState.Open;

        public IDbCommand CreateCommand() => new Command(_server);

        public IDbTransaction BeginTransaction() => new Transaction(_server, this);

        public IDbTransaction BeginTransaction(IsolationLevel il) => BeginTransaction();

        public void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public void Open()
        {
        }

        public void Close()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class Transaction : IDbTransaction
    {
        private readonly FakeDictionaryServer _server;

        public Transaction(FakeDictionaryServer server, IDbConnection connection)
        {
            _server = server;
            Connection = connection;
        }

        public IDbConnection Connection { get; }

        public IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        public void Commit() => _server.Committed = true;

        public void Rollback() => _server.RolledBack = true;

        public void Dispose()
        {
        }
    }

    private sealed class Command : IDbCommand
    {
        private readonly FakeDictionaryServer _server;

        public Command(FakeDictionaryServer server) => _server = server;

        [AllowNull] public string CommandText { get; set; } = string.Empty;

        public int CommandTimeout { get; set; }

        public CommandType CommandType { get; set; }

        public IDbConnection? Connection { get; set; }

        public IDbTransaction? Transaction { get; set; }

        public UpdateRowSource UpdatedRowSource { get; set; }

        public IDataParameterCollection Parameters => throw new NotSupportedException();

        public IDbDataParameter CreateParameter() => throw new NotSupportedException();

        public void Cancel()
        {
        }

        public void Prepare() => throw new NotSupportedException();

        public object ExecuteScalar() => throw new NotSupportedException();

        public IDataReader ExecuteReader(CommandBehavior behavior) => ExecuteReader();

        public IDataReader ExecuteReader()
        {
            if (Transaction is null) throw new InvalidOperationException("併發檢查要在寫入的同一個交易裡。");

            using var table = new DataTable();
            table.Columns.Add("change_index", typeof(int));
            table.Columns.Add("current_value", typeof(string));

            foreach (Match match in ChangeLine.Matches(CommandText))
            {
                var key = match.Groups["schema"].Value + "." + match.Groups["object"].Value +
                          (match.Groups["column"].Success ? "." + match.Groups["column"].Value : "");
                if (_server.Descriptions.TryGetValue(key, out var value))
                {
                    table.Rows.Add(int.Parse(match.Groups["index"].Value), value);
                }
            }

            return table.CreateDataReader();
        }

        public int ExecuteNonQuery()
        {
            if (Transaction is null) throw new InvalidOperationException("寫入要在交易裡。");
            if (_server.DenyWrite is { } message) throw new DeniedException(message);

            _server.Executed.Add(CommandText);
            return -1;
        }

        public void Dispose()
        {
        }
    }

    private sealed class DeniedException : DbException
    {
        public DeniedException(string message)
            : base(message)
        {
        }
    }
}
