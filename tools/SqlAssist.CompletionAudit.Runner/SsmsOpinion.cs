using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.SmoMetadataProvider;
using Microsoft.SqlServer.Management.SqlParser.Binder;
using Microsoft.SqlServer.Management.SqlParser.Common;
using Microsoft.SqlServer.Management.SqlParser.Intellisense;
using Microsoft.SqlServer.Management.SqlParser.MetadataProvider;
using Microsoft.SqlServer.Management.SqlParser.Parser;

namespace SqlAssist.CompletionAudit.Runner;

/// <summary>
/// 第二意見：SSMS 自己的 Resolver 在同一個位置列不列得出同一個詞。
/// </summary>
/// <remarks>
/// 只在漏的位置問，而且只問名稱：沒有繫結時 <c>FindCompletions</c> 一項都不回，繫結後回的是物件、欄位、
/// 函式與日期部分，從來不回關鍵字（探針實測）。繫結要連到伺服器的 SmoMetadataProvider，第一次約兩秒，
/// 之後每次幾毫秒到幾十毫秒。
///
/// Provider 與 Binder 不是執行緒安全的，一個資料庫一條佇列；每次呼叫有逾時，逾時就這一筆不給意見、
/// 不等它（做法同 sqltoolsservice 的 BindingQueue）。
/// </remarks>
internal sealed class SsmsOpinion : IDisposable
{
    private static readonly ParseOptions Options = new("GO", true, DatabaseCompatibilityLevel.Current, TransactSqlVersion.Current);

    private static readonly TimeSpan FirstTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly AuditConnection _connection;
    private readonly string _database;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MetadataDisplayInfoProvider _display = new();
    private ServerConnection? _server;
    private IBinder? _binder;
    private bool _failed;
    private int _timeouts;

    public SsmsOpinion(AuditConnection connection, string database)
    {
        _connection = connection;
        _database = database;
    }

    /// <summary>逾時而沒給意見的次數。</summary>
    public int Timeouts => Volatile.Read(ref _timeouts);

    /// <summary>建立繫結失敗的原因；沒失敗是 null。</summary>
    public string? Failure { get; private set; }

    /// <summary>在 <paramref name="prefix"/> 的結尾，Resolver 的清單裡有沒有 <paramref name="word"/>；給不出意見時是 null。</summary>
    public async Task<bool?> ListsAsync(string prefix, string word, CancellationToken cancellationToken)
    {
        if (_failed)
        {
            return null;
        }

        var timeout = _binder is null ? FirstTimeout : Timeout;

        if (!await _gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _timeouts);
            return null;
        }

        var work = Task.Run(() => Find(prefix, word));
        var finished = await Task.WhenAny(work, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);

        if (finished != work)
        {
            // 佇列等它做完才放行；這一筆不給意見。
            Interlocked.Increment(ref _timeouts);
            _ = work.ContinueWith(_ => _gate.Release(), TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        _gate.Release();

        try
        {
            return await work.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_binder is null)
            {
                _failed = true;
                Failure = exception.Message;
            }

            return null;
        }
    }

    private bool Find(string prefix, string word)
    {
        if (_binder is null)
        {
            _server = new ServerConnection(_connection.Open(_database));
            _binder = BinderProvider.CreateBinder(SmoMetadataProvider.CreateConnectedProvider(_server));
        }

        var parse = Parser.Parse(prefix, Options);
        _binder.Bind(new[] { parse }, _database, BindMode.Batch);

        var line = 1;
        var lastBreak = -1;

        for (var index = 0; index < prefix.Length; index++)
        {
            if (prefix[index] == '\n')
            {
                line++;
                lastBreak = index;
            }
        }

        var target = AuditText.Normalize(word);
        var list = Resolver.FindCompletions(parse, line, prefix.Length - lastBreak, _display);
        return list.Any(declaration => string.Equals(AuditText.LastPart(declaration.Title), target, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _server?.Disconnect();
        _gate.Dispose();
    }
}
