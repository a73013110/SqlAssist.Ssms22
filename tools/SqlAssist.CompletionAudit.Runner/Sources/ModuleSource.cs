using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;

namespace SqlAssist.CompletionAudit.Runner.Sources;

/// <summary>資料庫裡程序、檢視、函式與觸發程序的定義（<c>sys.sql_modules</c>）。</summary>
/// <remarks>
/// 只要 CONNECT 與 VIEW DEFINITION；加密的模組定義是 NULL，略過。一個資料庫連不上不影響其他的。
/// </remarks>
internal sealed class ModuleSource : IAuditCorpusSource
{
    public const string SourceName = "modules";

    private const string Query = @"
SELECT s.name, o.name, m.definition
FROM sys.sql_modules AS m
JOIN sys.objects AS o ON o.object_id = m.object_id
JOIN sys.schemas AS s ON s.schema_id = o.schema_id
WHERE o.type IN ('P', 'V', 'FN', 'IF', 'TF', 'TR') AND o.is_ms_shipped = 0 AND m.definition IS NOT NULL
ORDER BY s.name, o.name;";

    private readonly AuditConnection _connection;
    private readonly IReadOnlyList<string> _databases;
    private readonly Action<string> _log;

    public ModuleSource(AuditConnection connection, IReadOnlyList<string> databases, Action<string> log)
    {
        _connection = connection;
        _databases = databases;
        _log = log;
    }

    public string Name => SourceName;

    /// <summary>連不上的資料庫；報告的總結列寫出來。</summary>
    public List<string> Failures { get; } = new();

    public IEnumerable<AuditFragment> Read(CancellationToken cancellationToken)
    {
        var fragments = new List<AuditFragment>();

        foreach (var database in _databases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // 走 System.Data 介面：SqlClient 的具體型別會把 netstandard 的外觀組件拉進編譯。
                using IDbConnection connection = _connection.Open(database);
                using var command = connection.CreateCommand();
                command.CommandText = Query;
                command.CommandTimeout = 120;
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    fragments.Add(new AuditFragment(
                        SourceName,
                        $"{SourceName}:{database}.{reader.GetString(0)}.{reader.GetString(1)}",
                        reader.GetString(2),
                        database));
                }
            }
            catch (DbException exception)
            {
                Failures.Add(database);
                _log($"模組定義：{database} 讀取失敗（{exception.Message}）");
            }
        }

        return fragments;
    }
}
