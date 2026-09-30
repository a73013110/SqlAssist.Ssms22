using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Completion;

/// <summary>
/// 以一份 <see cref="SqlMetadataCatalog"/> 回答建議清單的資料庫那一份。
/// </summary>
/// <remarks>
/// 目錄怎麼來由呼叫端決定：產品從 SSMS 查詢視窗的連線解析（每一次都重新問，換過資料庫就是另一份），
/// 召回稽核直連一個資料庫。跨資料庫與跨伺服器的限定字一律經 <see cref="SqlMetadataCatalogRegistry"/>
/// 換目錄，查不到就沒有建議，絕不退回本地同名的物件。
/// </remarks>
public sealed class SqlCatalogCompletionMetadata : ISqlCompletionMetadata
{
    private static readonly IReadOnlyList<SqlSuggestion> Nothing = Array.Empty<SqlSuggestion>();

    private readonly Func<SqlMetadataCatalog?> _resolveCatalog;
    private readonly Action<string, Stopwatch>? _reportTiming;
    private readonly Action<string>? _trace;

    /// <param name="resolveCatalog">目前這條連線的目錄；沒有可用連線時回傳 null。</param>
    /// <param name="reportTiming">一次查詢做完時的耗時紀錄；只進診斷。</param>
    /// <param name="trace">背景預熱的紀錄；只進診斷。</param>
    public SqlCatalogCompletionMetadata(
        Func<SqlMetadataCatalog?> resolveCatalog,
        Action<string, Stopwatch>? reportTiming = null,
        Action<string>? trace = null)
    {
        _resolveCatalog = resolveCatalog ?? throw new ArgumentNullException(nameof(resolveCatalog));
        _reportTiming = reportTiming;
        _trace = trace;
    }

    /// <remarks>
    /// 只看文字時 <c>dbo.</c>、<c>LibArchive.</c> 與 <c>LIBSQL02.</c> 是同一個形狀，
    /// 建議清單、插入文字與目錄選擇卻要三種不同的答案。因此在<b>問清單之前</b>
    /// 先把上下文換成對齊過的那一個，後面三條路都讀同一份——各自再判一次的話，
    /// 症狀是清單列得出來、Tab 下去卻少一段。
    ///
    /// 讀的是本機第一層快照，也就是候選清單下一步無論如何都要載入的那一份，
    /// 所以這裡不會多送一輪查詢。刻意等它而不是只取已經快取的：查詢視窗剛開的
    /// 第一次補全還沒有快照，只取快取的症狀是「第一次沒有清單，再按一次才有」。
    ///
    /// 認不出來就維持右對齊的原判，也就是這個功能出現之前的行為。
    /// </remarks>
    public async Task<SqlCompletionContext> ResolveQualifierAsync(
        SqlCompletionContext context,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        // 限定字已經解析成別名時（u.），清單裡放的是欄位，那一段不是任何名稱空間。
        if (context.QualifierPath is not { } qualifier ||
            context.Target == CompletionTarget.Column)
        {
            return context;
        }

        if (_resolveCatalog() is not { } catalog)
        {
            return context;
        }

        var snapshot = await catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var resolved = SqlQualifierResolver.Resolve(qualifier, snapshot);

        return ReferenceEquals(resolved, qualifier) ? context : context.WithQualifierPath(resolved);
    }

    /// <remarks>
    /// 跨資料庫第一次一定要查一輪，之後就與本地的目錄一樣命中快取。只在<b>使用者真的打出資料庫名稱</b>
    /// 之後才走到這裡：預先把每一個進得去的資料庫都撈一份的話，共用主機上等於幾十輪查詢與
    /// 幾十份常駐快照，而其中九成九不會有人用到。
    /// </remarks>
    [Localizable(false)]
    public async Task<IReadOnlyList<SqlSuggestion>> GetObjectsAsync(
        SqlObjectPath? qualifierPath,
        CancellationToken cancellationToken)
    {
        var catalog = SqlMetadataCatalogRegistry.Default.ScopeTo(_resolveCatalog(), qualifierPath);

        if (catalog is null)
        {
            return Nothing;
        }

        var timer = Stopwatch.StartNew();
        var snapshot = await catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

        // 跨伺服器那一條的耗時由對方決定，紀錄裡要看得出是哪一台——本機慢與
        // 對面慢的處理方式完全不同，混成同一行等於每次都要再問一次。
        if (qualifierPath is { IsCrossServer: true })
        {
            _reportTiming?.Invoke(
                $"連結伺服器建議 {qualifierPath.ServerName}.{qualifierPath.DatabaseName}（第一層）",
                timer);
        }
        else if (qualifierPath is { IsCrossDatabase: true })
        {
            _reportTiming?.Invoke($"跨資料庫建議 {qualifierPath.DatabaseName}（第一層）", timer);
        }

        return SqlCatalogSuggestions.Objects(snapshot, includeLinkedServers: qualifierPath is null or { IsLocal: true });
    }

    /// <remarks>
    /// 呼叫端必須先確認這個位置真的要它——這一份有一兩千筆。第一次被問到才查資料庫，
    /// 之後整個工作階段都用快取。
    /// </remarks>
    [Localizable(false)]
    public async Task<IReadOnlyList<SqlSuggestion>> GetSystemObjectsAsync(
        SqlObjectPath? qualifierPath,
        CancellationToken cancellationToken)
    {
        if (SqlMetadataCatalogRegistry.Default.ScopeTo(_resolveCatalog(), qualifierPath) is not { } catalog)
        {
            return Nothing;
        }

        var timer = Stopwatch.StartNew();
        var objects = await catalog.GetSystemObjectsAsync(cancellationToken).ConfigureAwait(false);

        if (objects.Count == 0)
        {
            return Nothing;
        }

        var suggestions = SqlCatalogSuggestions.Objects(objects);
        _reportTiming?.Invoke($"系統物件建議（{suggestions.Count} 筆）", timer);
        return suggestions;
    }

    /// <remarks>落在第二層按需載入：一次只查一個物件的欄位。</remarks>
    [Localizable(false)]
    public async Task<IReadOnlyList<SqlSuggestion>> GetColumnsAsync(
        SqlTableReference table,
        SqlAssistSettings settings,
        CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();

        if (await SqlResolvedTable.ResolveAsync(_resolveCatalog(), table, cancellationToken).ConfigureAwait(false)
            is not { } resolved)
        {
            return Nothing;
        }

        _reportTiming?.Invoke(
            $"欄位建議 {resolved.Object.QualifiedName}" +
            $"（第二層{(resolved.DetailWasCached ? "命中快取" : "查詢資料庫")}）",
            total);

        return ToColumns(resolved, settings, qualifier: null);
    }

    public IReadOnlyList<SqlSuggestion> PeekColumns(SqlTableReference table, string? qualifier, SqlAssistSettings settings)
    {
        return SqlResolvedTable.TryPeek(_resolveCatalog(), table, out var resolved)
            ? ToColumns(resolved, settings, qualifier)
            : Nothing;
    }

    /// <remarks>
    /// 使用者輸入 <c>a.</c> 的那一刻才去查欄位，等待就完全落在打字的節奏上。
    /// 但在那之前他已經打過 <c>FROM PUBLISHER a</c>——那時就可以把敘述裡每一張資料表的欄位
    /// 先撈回來，等到真的按下點號時直接命中快取。失敗一律安靜略過：這只是預熱，
    /// 真正需要時還會再走一次正規路徑。
    /// </remarks>
    [Localizable(false)]
    public async Task WarmColumnsAsync(IReadOnlyList<SqlColumnSource> sources, CancellationToken cancellationToken)
    {
        if (sources is null || sources.Count == 0 || _resolveCatalog() is not { } editorCatalog)
        {
            return;
        }

        foreach (var source in sources)
        {
            // 子查詢與 CTE 的欄位名稱已經從指令碼讀出來了，沒有什麼好預熱的。
            if (source.Kind != SqlColumnSourceKind.Table)
            {
                continue;
            }

            var table = source.Table!;
            var catalog = SqlMetadataCatalogRegistry.Default.ScopeTo(editorCatalog, table.Path);

            if (catalog is null)
            {
                continue;
            }

            // 目前這條連線的第一層不在這裡觸發：那是建議清單自己的工作，
            // 這裡只是背景加速，不該再排一輪同樣的查詢。
            //
            // 跨資料庫的目錄相反——沒有別人會去載它。敘述已經把那個資料庫的
            // 名字寫出來了（FROM LibArchive.dbo.Loan l），等於使用者指名要它；
            // 從前要等他真的打出那一整串限定字才載，症狀是 SET | 與 WHERE |
            // 這種沒有限定字的位置永遠列不出跨庫來源的欄位，而同一份欄位
            // 打出 l. 就有。載一次就進快取，重複與失敗退避由這一支自己擋。
            if (!catalog.IsSnapshotFresh)
            {
                if (ReferenceEquals(catalog, editorCatalog))
                {
                    continue;
                }

                await catalog.WarmSnapshotAsync().ConfigureAwait(false);

                if (!catalog.IsSnapshotFresh)
                {
                    continue;
                }
            }

            // 第一層在上面已經確認新鮮，這一支不會多送一輪查詢；但敘述寫出
            // sys.triggers 時它會把系統物件那一份載進來——只讀快取的
            // PeekColumns 沒有別的機會等到它，症狀是 SELECT | 與
            // WHERE | 永遠列不出系統檢視的欄位，而打出 t. 卻列得出來。
            var matches = await catalog
                .FindObjectsAsync(table.ObjectName, table.SchemaName, cancellationToken)
                .ConfigureAwait(false);

            if (matches.Count == 0 || catalog.TryGetCachedDetail(matches[0].ObjectId, out _))
            {
                continue;
            }

            var timer = Stopwatch.StartNew();
            await catalog
                .GetDetailAsync(matches[0], cancellationToken, NotificationOrigin.Ambient)
                .ConfigureAwait(false);
            _trace?.Invoke($"已預先載入 {matches[0].QualifiedName} 的欄位（{timer.ElapsedMilliseconds} ms）");
        }
    }

    /// <remarks>
    /// 與欄位建議走同一條分層路徑：使用者真的打出小老鼠時才查一個物件的第二層。
    /// 查不到、或那個名稱不是可執行的模組時回傳空清單——這裡少列幾筆只是少了補字，
    /// 他自己的變數仍然照列。
    /// </remarks>
    [Localizable(false)]
    public async Task<IReadOnlyList<SqlSuggestion>> GetParametersAsync(
        SqlExecutedModule module,
        CancellationToken cancellationToken)
    {
        if (module is null || _resolveCatalog() is not { } catalog)
        {
            return Nothing;
        }

        var timer = Stopwatch.StartNew();
        var matches = await catalog
            .FindObjectsAsync(module.ObjectName, module.SchemaName, cancellationToken)
            .ConfigureAwait(false);

        if (matches.Count == 0 || !matches[0].Kind.IsExecutable())
        {
            return Nothing;
        }

        var detail = await catalog
            .GetDetailAsync(matches[0], cancellationToken, NotificationOrigin.Typing)
            .ConfigureAwait(false);

        _reportTiming?.Invoke($"參數建議 {matches[0].QualifiedName}（第二層）", timer);

        return detail is { Parameters.Count: > 0 }
            ? SqlCatalogSuggestions.Parameters(matches[0], detail)
            : Nothing;
    }

    /// <remarks>
    /// 一律問目前這條連線的目錄，不跟著限定字換——名單屬於<b>執行個體</b>，
    /// 而使用者正在編輯的這份指令碼跑在那條連線上。跨資料庫或跨伺服器的目錄
    /// 回答的是別台機器支援什麼，選中的名稱在這裡可能根本不存在。
    /// </remarks>
    [Localizable(false)]
    public async Task<SqlInstanceListData> GetInstanceListAsync(
        SqlInstanceList list,
        CancellationToken cancellationToken)
    {
        if (list is null)
        {
            throw new ArgumentNullException(nameof(list));
        }

        if (_resolveCatalog() is not { } catalog)
        {
            return SqlInstanceListData.Empty;
        }

        var timer = Stopwatch.StartNew();
        var data = await catalog.GetInstanceListAsync(list, cancellationToken).ConfigureAwait(false);
        _reportTiming?.Invoke($"{list.Target} 名單（{data.Entries.Count} 筆）", timer);
        return data;
    }

    private static IReadOnlyList<SqlSuggestion> ToColumns(
        SqlResolvedTable resolved,
        SqlAssistSettings settings,
        string? qualifier)
    {
        if (resolved.Detail is not { Columns.Count: > 0 } detail)
        {
            return Nothing;
        }

        var suggestions = new SqlSuggestion[detail.Columns.Count];

        for (var index = 0; index < suggestions.Length; index++)
        {
            suggestions[index] = SqlCatalogSuggestions.Column(resolved.Object, detail.Columns[index], settings, qualifier);
        }

        return suggestions;
    }
}
