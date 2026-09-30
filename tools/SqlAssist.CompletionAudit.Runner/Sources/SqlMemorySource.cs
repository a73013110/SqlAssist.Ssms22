using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SqlAssist.Core.SqlMemory;
using SqlAssist.SqlMemory.Isolation;

namespace SqlAssist.CompletionAudit.Runner.Sources;

/// <summary>
/// SQL Memory 的執行歷史：使用者真的跑過的指令碼，同一份內容只取一次。
/// </summary>
/// <remarks>
/// 路徑與開啟方式都是擴充自己那一份（<see cref="SqlMemoryLocation"/>、<see cref="IsolatedSqlMemoryStore"/>），
/// 只呼叫讀取；檔案不存在就不開，否則儲存層會替它建一個新的。草稿不讀：多半寫到一半。
/// 歷史上記的資料庫是稽核連得到的那幾個之一時才配中繼資料，其餘的名稱查不到存在、不稽核。
/// </remarks>
internal sealed class SqlMemorySource : IAuditCorpusSource
{
    public const string SourceName = "memory";

    private readonly string _ide;
    private readonly IReadOnlyCollection<string> _databases;
    private readonly Action<string> _log;

    public SqlMemorySource(string ide, IReadOnlyCollection<string> databases, Action<string> log)
    {
        _ide = ide;
        _databases = databases;
        _log = log;
    }

    public string Name => SourceName;

    public IEnumerable<AuditFragment> Read(CancellationToken cancellationToken)
    {
        var path = SqlMemoryLocation.DatabasePath();

        if (!File.Exists(path))
        {
            _log($"SQL Memory：找不到 {path}，略過。");
            return Array.Empty<AuditFragment>();
        }

        var fragments = new List<AuditFragment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        using var store = IsolatedSqlMemoryStore.OpenAsync(path, _ide, cancellationToken).GetAwaiter().GetResult();
        string? cursor = null;

        do
        {
            var page = store
                .ReadHistoryAsync(new SqlHistoryRequest(200, SqlHistoryFilter.Executions, cursor: cursor), cancellationToken)
                .GetAwaiter()
                .GetResult();

            foreach (var item in page.Items)
            {
                if (!seen.Add(item.ContentId) ||
                    store.ReadContentAsync(item.ContentId, cancellationToken).GetAwaiter().GetResult() is not { } content)
                {
                    continue;
                }

                var database = item.Connection?.Database is { } name
                    ? _databases.FirstOrDefault(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
                    : null;
                fragments.Add(new AuditFragment(SourceName, $"{SourceName}:{content.ContentHash}", content.SqlText, database));
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return fragments;
    }
}
