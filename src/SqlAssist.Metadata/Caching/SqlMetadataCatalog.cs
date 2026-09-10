using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Core.Notifications;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Caching;

/// <summary>
/// 單一「伺服器＋資料庫」的中繼資料快取與分層載入協調者。
/// </summary>
/// <remarks>
/// 分成三層是為了讓第一次按鍵的成本與資料庫大小脫鉤：
/// 第一層只取物件名稱，第二層在使用者選取某個物件時才取欄位與參數，
/// 第三層的定義本文則等到真的要顯示或要展開 ALTER 時才取。
/// </remarks>
public sealed class SqlMetadataCatalog
{
    /// <summary>明細快取的上限。超過時整批清掉，換取實作簡單與可預期的記憶體用量。</summary>
    private const int MaximumCachedDetails = 256;

    /// <summary>載入失敗之後隔多久才願意再試一次。</summary>
    /// <remarks>
    /// 短到使用者修好連線之後不會覺得「怎麼還是沒有」，長到一輪按鍵不會撞第二次。
    /// </remarks>
    private static readonly TimeSpan DefaultFailureBackoff = TimeSpan.FromSeconds(20);

    /// <summary>跨到別台伺服器時的命令逾時，比本機短。</summary>
    /// <remarks>
    /// 延遲與可用性由<b>對方那台伺服器</b>決定，而載入閘是每個目錄一把——等滿本機
    /// 那個逾時只是讓「這一格沒有建議」晚很久才確定下來。配合失敗退避，
    /// 對面不通最多讓使用者等這麼久一次。
    /// </remarks>
    private const int RemoteCommandTimeoutSeconds = 8;

    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly object _detailLock = new();
    private readonly Dictionary<int, SqlObjectDetail> _details = new();
    private readonly Dictionary<int, SqlObjectStructure> _structures = new();
    private readonly ISqlConnectionSource _connectionSource;
    private readonly TimeSpan _lifetime;
    private readonly int _commandTimeoutSeconds;
    private readonly TimeSpan _failureBackoff;
    private readonly SqlCatalogQualifier _qualifier;
    private SqlDatabaseSnapshot _snapshot = SqlDatabaseSnapshot.Empty;
    private int _snapshotVersion;

    /// <summary>上一次載入失敗的時刻；沒有失敗過時為 0。</summary>
    private long _failedAtTicks;

    private readonly SemaphoreSlim _systemGate = new(1, 1);

    /// <summary>系統物件；只有真的被問到才載入，見 <see cref="GetSystemObjectsAsync"/>。</summary>
    private IReadOnlyList<SqlObjectInfo>? _systemObjects;

    private readonly SemaphoreSlim _collationGate = new(1, 1);

    /// <summary>定序；只有游標落在 <c>COLLATE</c> 之後才載入，見 <see cref="GetCollationsAsync"/>。</summary>
    private SqlCollations? _collations;

    public SqlMetadataCatalog(
        ISqlConnectionSource connectionSource,
        TimeSpan lifetime,
        int commandTimeoutSeconds = 15,
        TimeSpan? failureBackoff = null,
        SqlCatalogQualifier? qualifier = null)
    {
        _connectionSource = connectionSource ?? throw new ArgumentNullException(nameof(connectionSource));
        _lifetime = lifetime;
        _commandTimeoutSeconds = commandTimeoutSeconds;
        _failureBackoff = failureBackoff ?? DefaultFailureBackoff;
        _qualifier = qualifier ?? SqlCatalogQualifier.Local;
    }

    public string CacheKey => _connectionSource.CacheKey;

    /// <summary>
    /// 這份目錄實際使用的連線來源；要換到別的資料庫或別台伺服器時從這裡取。
    /// </summary>
    /// <remarks>
    /// 連線來源的所有權在 <see cref="SqlMetadataCatalogRegistry"/>：同一個快取鍵重複
    /// 建立時多出來的那一份會被釋放，而交出去的呼叫端無從得知留下來的是不是自己那一份。
    /// 因此呼叫端<b>禁止</b>自己留一份來源，一律從手上這份目錄取。
    ///
    /// 自己留的症狀是：使用者先打過 <c>LibArchive.dbo.</c>（建了那個資料庫的目錄），
    /// 再 <c>USE LibArchive</c> 切過去，這一次交出去的來源就是多出來的那一份、當場被
    /// 釋放，而呼叫端手上還握著它——之後每一個限定名稱都以 ObjectDisposedException
    /// 收場，連線沒有變過也就再也不會重建，直到關掉查詢視窗為止。
    /// </remarks>
    public ISqlConnectionSource ConnectionSource => _connectionSource;

    /// <summary>目前已快取的第一層資料；尚未載入時為空快照。呼叫端可用它先畫出清單。</summary>
    public SqlDatabaseSnapshot CachedSnapshot => Volatile.Read(ref _snapshot);

    /// <summary>清空所有層級的快取，下一次查詢會重新讀取資料庫。</summary>
    public void Invalidate()
    {
        // 系統物件沒有有效期，只有這裡會把它丟掉——換連線就是換一台伺服器。
        Volatile.Write(ref _systemObjects, null);

        lock (_detailLock)
        {
            // 清除期間仍在飛的第一層查詢不得把舊清單寫回來。
            _snapshotVersion++;
            Volatile.Write(ref _snapshot, SqlDatabaseSnapshot.Empty);
            Volatile.Write(ref _failedAtTicks, 0);
            _details.Clear();
            _structures.Clear();
        }
    }

    /// <summary>
    /// 只清掉單一物件的第二、四層快取。
    /// </summary>
    /// <remarks>
    /// 使用者在結構面板按重新整理時要的是「這一張表」，
    /// 沒有理由連整個資料庫的物件清單一起丟掉，那會讓下一次按鍵重新等一輪查詢。
    /// </remarks>
    public void InvalidateObject(int objectId)
    {
        lock (_detailLock)
        {
            _details.Remove(objectId);
            _structures.Remove(objectId);
        }
    }

    /// <summary>第一層資料是否仍在有效期內。</summary>
    public bool IsSnapshotFresh => IsFresh(CachedSnapshot);

    /// <summary>
    /// 取得第一層資料。
    /// </summary>
    /// <remarks>
    /// 已有資料但過期時<b>先回傳舊的、同時在背景更新</b>，不讓使用者為了重新整理而等待。
    /// 物件清單過期五分鐘與剛好新增了一張資料表相比，前者的代價遠低於每五分鐘
    /// 就有一次按鍵要等一輪資料庫查詢——而那一輪還會擋在欄位建議的前面。
    /// 只有完全沒有資料時才真的等。
    /// </remarks>
    public async Task<SqlDatabaseSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var cached = CachedSnapshot;

        if (IsFresh(cached))
        {
            NoteCacheHit(NotificationOrigin.Typing, cached.DatabaseName);
            return cached;
        }

        // 剛失敗過就先不試。失敗的結果刻意不進快取（連線恢復之後才不會卡在空的），
        // 而空快照永遠不算新鮮——兩條加起來，連不上的目標會變成「每一次按鍵重開一條
        // 連線去撞同一堵牆」，而每一次都要等滿命令逾時。使用者看到的是打字整個卡住。
        if (IsInFailureBackoff())
        {
            return cached;
        }

        if (!cached.IsEmpty)
        {
            _ = RefreshSnapshotInBackgroundAsync();
            return cached;
        }

        // 多個查詢視窗同時開啟時，只讓一條執行緒真的去查資料庫。
        await _snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (IsFresh(CachedSnapshot) || IsInFailureBackoff())
            {
                return CachedSnapshot;
            }

            var version = Volatile.Read(ref _snapshotVersion);
            // 前景載入永遠是打字打出來的：使用者按了鍵、清單要資料，才會走到這裡。
            return await Task.Run(
                    () => LoadAndPublishSnapshot(cancellationToken, version, NotificationOrigin.Typing),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    /// <summary>
    /// 跑一次載入；資料庫本身說不行時回傳 null。
    /// </summary>
    /// <remarks>
    /// 連不上、逾時、權限不足、物件剛被砍掉——這一類失敗<b>不可以</b>冒到
    /// Ssms22 的平台邊界。那裡的 <c>SqlAssistPlatformGuard</c> 會把每一次都記成一份
    /// 完整堆疊，而連線斷掉時使用者每開一次建議清單就失敗一次；紀錄檔被灌滿之後，
    /// 真正的程式錯誤就埋在裡面找不到了。降級成「這一輪沒有資料」，
    /// 呼叫端本來就分得出空與有。
    ///
    /// 只接 <see cref="DbException"/>。連線字串寫錯、參數契約違反與其餘任何例外
    /// 都是程式錯誤，該讓它一路浮到邊界去——那正是要留下完整堆疊的那一種。
    ///
    /// 失敗的結果一律不進快取：那會讓連線恢復之後仍然拿到空的。
    /// </remarks>
    private void RecordFailure()
    {
        Volatile.Write(ref _failedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    private bool IsInFailureBackoff()
    {
        var failedAt = Volatile.Read(ref _failedAtTicks);

        return failedAt != 0 &&
               DateTimeOffset.UtcNow.UtcTicks - failedAt < _failureBackoff.Ticks;
    }

    /// <param name="operation">
    /// 哪一條查詢。降級之後畫面上分不出「連線斷了」與「這條查詢寫錯了」，
    /// 而紀錄檔裡唯一分得出來的線索就是這個名稱加上伺服器說的那句話；
    /// 送出去的地方見 <see cref="SqlMetadataFailure"/>。
    /// </param>
    /// <param name="origin">
    /// 誰觸發了這一次載入。打字路徑與預載在畫面上的降噪門檻不同，而併進外層工作的
    /// 巢狀查詢沿用外層那一份。
    /// </param>
    /// <param name="subject">物件限定名稱；清單類的查詢沒有單一主體，留空。</param>
    private T? TryLoad<T>(string operation, NotificationOrigin origin, Func<T> load, string subject = "")
        where T : class
    {
        // 三軸明寫：中繼資料查詢一律是 Metadata，等級一律 Info——比 Info 低的只有
        // 快取命中，而那一條根本不查資料庫。標題是常數，主體與來源是既有字串的引用，
        // 因此通知被可見度篩掉時這一段一個字串都不配置。
        using var notification = NotificationCenter.Default.Begin(operation, NotificationKind.Metadata,
            origin, NotificationLevel.Info, subject, ContextName, joinParent: true);
        // 遠端跳躍只由最外層那一次說明；巢狀查詢已經併進外層，跟著開會讓同一次載入
        // 冒出好幾列一模一樣的提示。
        using var hop = notification.OwnsItem ? BeginHop(origin) : null;
        try
        {
            return load();
        }
        catch (DbException exception)
        {
            notification.Fail();
            hop?.Degrade();
            // 紀錄檔要分得出「哪一條查詢」加「哪一個物件」，而通知的標題已經不含物件
            // 名稱了。組字串只在真的失敗時付一次，熱路徑一個字串都不配置。
            SqlMetadataFailure.Report(
                subject.Length == 0 ? operation : operation + "：" + subject, exception);
            return null;
        }
        catch (OperationCanceledException)
        {
            notification.Cancel();
            hop?.Cancel();
            throw;
        }
        catch
        {
            // 只標記結果，不改變非資料庫例外原本的傳播契約。
            notification.Fail();
            hop?.Fail();
            throw;
        }
    }

    /// <summary>畫面上「這一份目錄是從哪裡來的」；不隨焦點更新，也不放路徑或認證。</summary>
    private string ContextName => _qualifier.DatabaseName ?? _connectionSource.DatabaseName;

    /// <summary>
    /// 這一次查詢跳到了別的地方時的說明；就在本機同一個資料庫上時回傳 null。
    /// </summary>
    /// <remarks>
    /// 兩者都慢，而畫面上原本完全看不出來——<c>Context</c> 只有資料庫名，
    /// 跨庫與遠端跳躍看起來與本機查詢一模一樣。連結伺服器用 <c>Notice</c>：
    /// 它慢得使用者一定會發現，而「建議清單怎麼卡住了」這句問題只有這一列答得出來。
    /// </remarks>
    private NotificationScope? BeginHop(NotificationOrigin origin)
    {
        if (_qualifier.ServerName is { } serverName)
        {
            return NotificationCenter.Default.BeginDetached(NotificationCatalog.QueryingLinkedServer,
                NotificationKind.Metadata, origin, NotificationLevel.Notice, serverName, ContextName);
        }

        // 換連線的資料庫是跨資料庫的唯一形狀，見 SqlDatabaseScopedConnectionSource。
        return _connectionSource is SqlDatabaseScopedConnectionSource
            ? NotificationCenter.Default.BeginDetached(NotificationCatalog.ConnectingToDatabase,
                NotificationKind.Metadata, origin, NotificationLevel.Info,
                _connectionSource.DatabaseName, ContextName)
            : null;
    }

    /// <summary>
    /// 記一次快取命中；只進診斷統計，永不上畫面。
    /// </summary>
    /// <remarks>
    /// <c>Trace</c> 只有詳細度明選「全部」才放行。少了這一條就答不出「哪些動作在重複」
    /// ——畫面上只看得到真的查了資料庫的那幾次，而重複的按鍵大多是命中快取。
    /// </remarks>
    private void NoteCacheHit(NotificationOrigin origin, string subject)
    {
        using (NotificationCenter.Default.Begin(NotificationCatalog.CacheHit, NotificationKind.Metadata,
                   origin, NotificationLevel.Trace, subject, ContextName))
        {
        }
    }

    /// <summary>非阻塞地預載第一層；新鮮、退避或已有查詢在途時不另排工作。</summary>
    /// <remarks>回傳本次啟動的工作供平台觀察例外；不等待其他呼叫端已啟動的載入。</remarks>
    public Task WarmSnapshotAsync()
    {
        if (IsSnapshotFresh || IsInFailureBackoff() || !_snapshotGate.Wait(0))
        {
            return Task.CompletedTask;
        }

        // 取得閘之前，另一條前景或預載查詢可能剛好完成。
        if (IsSnapshotFresh || IsInFailureBackoff())
        {
            _snapshotGate.Release();
            return Task.CompletedTask;
        }

        var version = Volatile.Read(ref _snapshotVersion);
        return Task.Run(() =>
        {
            try
            {
                // 沒有人要求的預載；跨不過降噪門檻，但仍然計數與寫診斷。
                LoadAndPublishSnapshot(CancellationToken.None, version, NotificationOrigin.Ambient);
            }
            finally
            {
                _snapshotGate.Release();
            }
        });
    }

    private SqlDatabaseSnapshot LoadAndPublishSnapshot(CancellationToken cancellationToken, int version,
        NotificationOrigin origin)
    {
        var loaded = TryLoad(NotificationCatalog.LoadingObjects, origin, () => LoadSnapshot(cancellationToken));
        lock (_detailLock)
        {
            if (_snapshotVersion != version)
            {
                return CachedSnapshot;
            }

            if (loaded is null)
            {
                // 失敗不覆蓋舊快照；預載與前景讀取共用同一份退避。
                RecordFailure();
            }
            else
            {
                Volatile.Write(ref _snapshot, loaded);
                Volatile.Write(ref _failedAtTicks, 0);
            }

            return CachedSnapshot;
        }
    }

    private async Task RefreshSnapshotInBackgroundAsync()
    {
        try
        {
            await WarmSnapshotAsync().ConfigureAwait(false);
        }
        catch
        {
            // 舊快照更新沒有等待者；維持既有降級，契約錯誤留給下次前景載入重現。
        }
    }

    /// <summary>
    /// 取得系統物件；第一次被問到才查資料庫。
    /// </summary>
    /// <remarks>
    /// 刻意不放進第一層快照：這一份光是一個使用者資料庫底下就有一兩千列，
    /// 而它只在使用者打出 <c>sys.</c> 或落在 <c>EXEC </c> 之後才用得到。
    /// 併進去等於每一次開啟查詢視窗都多付兩倍代價，換來的東西九成的時間沒有人要。
    ///
    /// 也刻意<b>不設有效期</b>：系統物件跟著 SQL Server 的版本走，
    /// 不會在一次工作階段中途變動。查一次就用到編輯器關掉為止。
    ///
    /// 查不到時回傳空清單並且<b>不</b>記進快取，與第一層同一條規則：
    /// 連線恢復之後下一次會自然再試一次。
    /// </remarks>
    public async Task<IReadOnlyList<SqlObjectInfo>> GetSystemObjectsAsync(
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _systemObjects) is { } cached)
        {
            return cached;
        }

        await _systemGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Volatile.Read(ref _systemObjects) is { } raced)
            {
                return raced;
            }

            var loaded = await Task
                .Run(
                    () => TryLoad(NotificationCatalog.LoadingSystemObjects, NotificationOrigin.Typing,
                        () => LoadSystemObjects(cancellationToken)),
                    cancellationToken)
                .ConfigureAwait(false);

            if (loaded is null)
            {
                return Array.Empty<SqlObjectInfo>();
            }

            Volatile.Write(ref _systemObjects, loaded);
            return loaded;
        }
        finally
        {
            _systemGate.Release();
        }
    }

    /// <summary>
    /// 取得定序名單與目前資料庫的定序；第一次被問到才查資料庫。
    /// </summary>
    /// <remarks>
    /// 與系統物件同一種處境：一份幾千筆、只有一個位置用得到、而且不會在一次
    /// 工作階段中途變動的清單，因此同樣只在真的被問到時才載入、不設有效期。
    /// 差別在快取的層級——名單屬於伺服器而不是資料庫，跨目錄共用，
    /// 見 <see cref="SqlServerCollationCache"/>。
    ///
    /// 查不到時回傳 <see cref="SqlCollations.Empty"/> 並且<b>不</b>記進快取，
    /// 與其他層同一條規則。那個位置不會因此空掉：<c>DATABASE_DEFAULT</c> 與
    /// 這份指令碼已經寫過的定序都不必問伺服器，由 Core 那一側補上。
    ///
    /// 連結伺服器的目錄一律回傳空的：定序屬於執行個體，而使用者正在編輯的
    /// 這份指令碼跑在<b>本機</b>那條連線上。把對面那台的名單列出來，
    /// 選中的每一個名稱都可能在這裡不存在，而畫面上看不出差別。
    /// </remarks>
    public async Task<SqlCollations> GetCollationsAsync(CancellationToken cancellationToken)
    {
        if (_qualifier.IsRemote)
        {
            return SqlCollations.Empty;
        }

        if (Volatile.Read(ref _collations) is { } cached)
        {
            return cached;
        }

        await _collationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Volatile.Read(ref _collations) is { } raced)
            {
                return raced;
            }

            var loaded = await Task
                .Run(
                    () => TryLoad(NotificationCatalog.LoadingCollations, NotificationOrigin.Typing,
                        () => LoadCollations(cancellationToken)),
                    cancellationToken)
                .ConfigureAwait(false);

            if (loaded is null)
            {
                return SqlCollations.Empty;
            }

            SqlServerCollationCache.Set(_connectionSource.ServerCacheKey, loaded.Names);
            Volatile.Write(ref _collations, loaded);
            return loaded;
        }
        finally
        {
            _collationGate.Release();
        }
    }

    /// <summary>不觸發查詢，只看第二層快取裡有沒有。</summary>
    public bool TryGetCachedDetail(int objectId, out SqlObjectDetail detail)
    {
        lock (_detailLock)
        {
            return _details.TryGetValue(objectId, out detail!);
        }
    }

    /// <summary>不觸發查詢，只看第四層快取裡有沒有。</summary>
    public bool TryGetCachedStructure(int objectId, out SqlObjectStructure structure)
    {
        lock (_detailLock)
        {
            return _structures.TryGetValue(objectId, out structure!);
        }
    }

    /// <summary>取得單一物件的欄位、參數與定義；結果會被快取。</summary>
    /// <returns>資料庫取不到時為 <c>null</c>；理由見 <see cref="TryLoad{T}"/>。</returns>
    /// <param name="origin">
    /// 誰觸發了這一次；同一個查詢在打字路徑與使用者按下去的命令上該有不同的降噪門檻。
    /// </param>
    public async Task<SqlObjectDetail?> GetDetailAsync(
        SqlObjectInfo objectInfo,
        CancellationToken cancellationToken,
        NotificationOrigin origin)
    {
        if (objectInfo is null)
        {
            throw new ArgumentNullException(nameof(objectInfo));
        }

        SqlObjectDetail? hit;

        lock (_detailLock)
        {
            _details.TryGetValue(objectInfo.ObjectId, out hit);
        }

        if (hit is not null)
        {
            // 記在鎖外：通知會廣播出去，而那條路上有 UI 派送——不該壓在明細鎖裡。
            NoteCacheHit(origin, objectInfo.QualifiedName);
            return hit;
        }

        var detail = await Task
            .Run(
                () => TryLoad(
                    NotificationCatalog.LoadingColumns,
                    origin,
                    () => LoadDetail(objectInfo, cancellationToken),
                    objectInfo.QualifiedName),
                cancellationToken)
            .ConfigureAwait(false);

        if (detail is null)
        {
            return null;
        }

        lock (_detailLock)
        {
            if (_details.Count >= MaximumCachedDetails)
            {
                _details.Clear();
            }

            _details[objectInfo.ObjectId] = detail;
        }

        return detail;
    }

    /// <summary>
    /// 取得單一物件的完整結構：第二層的欄位與參數，加上索引與外來鍵。
    /// </summary>
    /// <remarks>
    /// 只有使用者主動打開結構面板時才會走到這裡，因此可以放心多查兩次；
    /// 按鍵路徑上的 <see cref="GetDetailAsync"/> 不受影響。
    /// </remarks>
    /// <returns>
    /// 第二層都取不到時為 <c>null</c>；理由見 <see cref="TryLoad{T}"/>。第二層拿到了
    /// 而第四層失敗時回傳一份標記為不完整的結構，不是 <c>null</c>——說明見下方。
    /// </returns>
    /// <param name="origin"><inheritdoc cref="GetDetailAsync" path="/param[@name='origin']"/></param>
    public async Task<SqlObjectStructure?> GetStructureAsync(
        SqlObjectInfo objectInfo,
        CancellationToken cancellationToken,
        NotificationOrigin origin)
    {
        if (objectInfo is null)
        {
            throw new ArgumentNullException(nameof(objectInfo));
        }

        SqlObjectStructure? hit;

        lock (_detailLock)
        {
            _structures.TryGetValue(objectInfo.ObjectId, out hit);
        }

        if (hit is not null)
        {
            NoteCacheHit(origin, objectInfo.QualifiedName);
            return hit;
        }

        if (await GetDetailAsync(objectInfo, cancellationToken, origin).ConfigureAwait(false) is not { } detail)
        {
            return null;
        }

        // 索引與外來鍵只有本身就是一張資料表的那幾類查得出東西。資料表值函式
        // 這一輪也有資料行了，但它的指令碼來自定義本文，索引寫不進
        // CREATE FUNCTION——為它多跑一次第四層查詢，換不到任何顯示得出來的分頁。
        if (!objectInfo.Kind.IsTableShaped())
        {
            return Cache(objectInfo, new SqlObjectStructure(detail));
        }

        var structure = await Task
            .Run(
                () => TryLoad(
                    NotificationCatalog.LoadingIndexes,
                    origin,
                    () => LoadStructure(detail, cancellationToken),
                    objectInfo.QualifiedName),
                cancellationToken)
            .ConfigureAwait(false);

        // 第四層失敗不回 null。回 null 的話呼叫端分不出「連不上」與「這一條查詢
        // 壞了」，而結構預覽對 null 只有一句「沒有可用的連線」——第二層明明已經
        // 把欄位畫出來了，那句話會把它整片蓋掉，並且把使用者送去查一個好好的連線。
        // 改成回傳只有第二層、且標記為不完整的結構：給人看的欄位留著，
        // 要拿去執行的指令碼由 CanBuildExecutableScript 擋下並說明原因。
        //
        // 這一份不進快取——失敗不進快取，否則連線恢復之後仍然拿到殘缺的那一份。
        return structure is null
            ? new SqlObjectStructure(detail, structureUnavailable: true)
            : Cache(objectInfo, structure);
    }

    private SqlObjectStructure Cache(SqlObjectInfo objectInfo, SqlObjectStructure structure)
    {
        lock (_detailLock)
        {
            if (_structures.Count >= MaximumCachedDetails)
            {
                _structures.Clear();
            }

            _structures[objectInfo.ObjectId] = structure;
        }

        return structure;
    }

    private SqlObjectStructure LoadStructure(SqlObjectDetail detail, CancellationToken cancellationToken)
    {
        using var connection = _connectionSource.OpenConnection();
        var objectId = detail.Object.ObjectId;

        var indexRows = ReadList(
            connection,
            SqlMetadataQueries.Indexes,
            SqlMetadataReader.ReadIndexRow,
            cancellationToken,
            objectId);

        // 檢視沒有外來鍵，少一次來回。
        var foreignKeyRows = detail.Object.Kind == SqlObjectKind.Table
            ? ReadList(
                connection,
                SqlMetadataQueries.ForeignKeys,
                SqlMetadataReader.ReadForeignKeyRow,
                cancellationToken,
                objectId)
            : new List<SqlForeignKeyRow>();

        // 擴充屬性與索引、外來鍵同一層，走同一條連線：分開載入等於在使用者
        // 打開結構的那一刻多開一次連線，而那三份資料一定是一起要的。
        var extendedProperties = ReadList(
            connection,
            SqlMetadataQueries.ExtendedProperties,
            SqlMetadataReader.ReadExtendedProperty,
            cancellationToken,
            objectId);

        // 檢視與資料表型別都沒有 CHECK 條件約束，少一次來回。
        var checkConstraints = detail.Object.Kind == SqlObjectKind.Table
            ? ReadList(
                connection,
                SqlMetadataQueries.CheckConstraints,
                SqlMetadataReader.ReadCheckConstraint,
                cancellationToken,
                objectId)
            : new List<SqlCheckConstraint>();

        // 只有資料表有檔案群組。檢視、資料表型別與指令碼宣告的東西問不到那一列，
        // 而查詢成功卻沒有列與查詢失敗在這裡是同一個結果：什麼都不寫。
        var storage = detail.Object.Kind == SqlObjectKind.Table
            ? ReadList(
                connection,
                SqlMetadataQueries.TableStorage,
                SqlMetadataReader.ReadTableStorage,
                cancellationToken,
                objectId)
            : new List<SqlTableStorage>();

        // 只有資料表掛得住觸發程序，而且只有指令碼要它——結構面板不列。
        var triggers = detail.Object.Kind == SqlObjectKind.Table
            ? ReadList(
                connection,
                SqlMetadataQueries.Triggers,
                SqlMetadataReader.ReadTrigger,
                cancellationToken,
                objectId)
            : new List<SqlTriggerInfo>();

        return new SqlObjectStructure(
            detail,
            SqlIndexInfo.FromRows(indexRows),
            SqlForeignKeyInfo.FromRows(foreignKeyRows),
            extendedProperties,
            checkConstraints,
            storage.Count > 0 ? storage[0] : SqlTableStorage.None,
            triggers);
    }

    private bool IsFresh(SqlDatabaseSnapshot snapshot)
    {
        return !snapshot.IsEmpty && DateTimeOffset.UtcNow - snapshot.LoadedAt < _lifetime;
    }

    private SqlDatabaseSnapshot LoadSnapshot(CancellationToken cancellationToken)
    {
        using var connection = _connectionSource.OpenConnection();

        // 連結伺服器本身那一格（LibMirror.）要的只有資料庫清單。物件與結構描述
        // 要再往右一格才問得到，在這裡先撈一份等於對那台伺服器多送兩輪
        // 誰也不會看的查詢——而那兩輪的延遲由對方決定。
        if (_qualifier.IsServerRoot)
        {
            return new SqlDatabaseSnapshot(
                string.Empty,
                Array.Empty<SqlObjectInfo>(),
                Array.Empty<string>(),
                ReadDatabases(connection, cancellationToken),
                DateTimeOffset.UtcNow);
        }

        // 每一筆都記下自己是從哪台伺服器的哪個資料庫來的。object_id 只在單一
        // 資料庫裡唯一，而下游（滑鼠停留、結構預覽、F12、提交後展開）拿著這個物件
        // 回頭要第二、三、四層時，必須換到同一份目錄才問得到對的東西。
        var databaseName = _qualifier.DatabaseName ?? _connectionSource.DatabaseName;

        var objects = ReadList(
                connection,
                SqlMetadataQueries.Objects,
                record => SqlMetadataReader.ReadObject(record, databaseName, _qualifier.ServerName),
                cancellationToken)
            .FindAll(info => info.Kind != SqlObjectKind.Unknown);

        var schemas = ReadList(
            connection,
            SqlMetadataQueries.Schemas,
            record => record.GetString(0),
            cancellationToken);

        // 連結伺服器上再掛的連結伺服器沒有用：T-SQL 沒有五段式名稱。
        return new SqlDatabaseSnapshot(
            databaseName,
            objects,
            schemas,
            ReadDatabases(connection, cancellationToken),
            DateTimeOffset.UtcNow,
            _qualifier.IsRemote ? null : ReadLinkedServers(connection, cancellationToken));
    }

    /// <remarks>
    /// 資料庫清單查不到不該讓整份快照失敗：權限不足時 sys.databases 仍會回傳
    /// 至少一列，但自訂的伺服器角色設定確實有可能整個擋掉。少了 USE 的建議
    /// 遠比整個物件清單都拿不到輕微。
    /// </remarks>
    private List<string> ReadDatabases(IDbConnection connection, CancellationToken cancellationToken)
    {
        return TryLoad(NotificationCatalog.LoadingDatabases, NotificationOrigin.Typing, () => ReadList(
            connection,
            SqlMetadataQueries.Databases,
            record => record.GetString(0),
            cancellationToken)) ?? new List<string>();
    }

    /// <remarks>
    /// 與資料庫清單同理，查不到就當成一台都沒掛。這條查的是<b>本機</b>的
    /// <c>sys.servers</c>，不對任何一台連結伺服器送出查詢——真正要跨過去的
    /// 是使用者打出那個名字之後的事。
    /// </remarks>
    private List<string> ReadLinkedServers(IDbConnection connection, CancellationToken cancellationToken)
    {
        return TryLoad(NotificationCatalog.LoadingLinkedServers, NotificationOrigin.Typing, () => ReadList(
            connection,
            SqlMetadataQueries.LinkedServers,
            record => record.GetString(0),
            cancellationToken)) ?? new List<string>();
    }

    private SqlCollations LoadCollations(CancellationToken cancellationToken)
    {
        using var connection = _connectionSource.OpenConnection();

        // 同一台伺服器已經問過就不再問第二次；那一份與連到哪個資料庫無關。
        var names = SqlServerCollationCache.TryGet(_connectionSource.ServerCacheKey, out var shared)
            ? shared
            : ReadList(
                connection,
                SqlMetadataQueries.Collations,
                record => record.GetString(0),
                cancellationToken);

        return new SqlCollations(names, ReadDatabaseCollation(connection, cancellationToken));
    }

    /// <remarks>
    /// 與資料庫清單同理：這一個查不到不該讓整份名單跟著沒有。少了它只是
    /// 「排在最前面的那一個不見了」，而整份名單沒有的話那個位置只剩兩個字。
    /// </remarks>
    private string? ReadDatabaseCollation(IDbConnection connection, CancellationToken cancellationToken)
    {
        var rows = TryLoad(NotificationCatalog.LoadingDatabaseCollation, NotificationOrigin.Typing, () => ReadList(
            connection,
            SqlMetadataQueries.DatabaseCollation,
            record => record.IsDBNull(0) ? null : record.GetString(0),
            cancellationToken));

        return rows is { Count: > 0 } ? rows[0] : null;
    }

    private List<SqlObjectInfo> LoadSystemObjects(CancellationToken cancellationToken)
    {
        using var connection = _connectionSource.OpenConnection();

        return ReadList(
                connection,
                SqlMetadataQueries.SystemObjects,
                record => SqlMetadataReader.ReadObject(record, _connectionSource.DatabaseName),
                cancellationToken)
            .FindAll(info => info.Kind != SqlObjectKind.Unknown);
    }

    private SqlObjectDetail LoadDetail(SqlObjectInfo objectInfo, CancellationToken cancellationToken)
    {
        using var connection = _connectionSource.OpenConnection();
        var objectId = objectInfo.ObjectId;

        var columns = objectInfo.Kind.HasCatalogColumns()
            ? ReadList(
                connection,
                SqlMetadataQueries.Columns,
                SqlMetadataReader.ReadColumn,
                cancellationToken,
                objectId)
            : new List<SqlColumnInfo>();

        // 說明與資料行同一層：滑鼠停留提示只讀快取、不等查詢，併進第四層的話
        // 提示上只有「剛好開過結構」的物件才有說明，而畫面上看不出那個差別。
        // 資料行的說明沒有這一次來回，它跟著 sys.columns 的 LEFT JOIN 一起回來。
        var description = ReadObjectDescription(connection, objectId, cancellationToken);

        if (!objectInfo.Kind.IsModule())
        {
            return new SqlObjectDetail(
                objectInfo,
                columns,
                new List<SqlParameterInfo>(),
                LoadSynthesizedDefinition(connection, objectInfo, cancellationToken),
                description);
        }

        var parameters = ReadList(
            connection,
            SqlMetadataQueries.Parameters,
            SqlMetadataReader.ReadParameter,
            cancellationToken,
            objectId);

        using var command = CreateCommand(connection, SqlMetadataQueries.Definition, objectId);
        var value = command.ExecuteScalar();
        var definition = value is string text && !string.IsNullOrWhiteSpace(text) ? text : null;

        return new SqlObjectDetail(objectInfo, columns, parameters, definition, description);
    }

    /// <remarks>
    /// 一列都沒有就是沒有掛說明，與查詢失敗分得開——失敗在
    /// <see cref="TryLoad{T}"/> 就整份第二層降級成「這一輪沒有資料」了，
    /// 走不到這裡。
    /// </remarks>
    private string? ReadObjectDescription(
        IDbConnection connection,
        int objectId,
        CancellationToken cancellationToken)
    {
        var rows = ReadList(
            connection,
            SqlMetadataQueries.ObjectDescription,
            record => record.IsDBNull(0) ? null : record.GetString(0),
            cancellationToken,
            objectId);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// 同義字與序列的定義：目錄檢視上的那幾個欄位，組成一段 <c>CREATE</c>。
    /// </summary>
    /// <remarks>
    /// 與模組的定義放進同一個欄位（<see cref="SqlObjectDetail.Definition"/>），
    /// 因為對所有下游而言它們就是同一件事：一段照著執行就會得到這個物件的 T-SQL。
    /// 分成兩個欄位的話，滑鼠停留提示、預覽的指令碼分頁與 F12 每一條都要多問一次
    /// 「這一種要看哪一個欄位」，而漏掉的那一條會安靜地退回「沒有定義」。
    ///
    /// 這兩支查詢只在<b>這一種物件</b>的細節被要求時才送出——多一次來回，
    /// 但那一次不在按鍵路徑上，而且同一個物件只會付一次（細節有快取）。
    /// </remarks>
    private string? LoadSynthesizedDefinition(
        IDbConnection connection,
        SqlObjectInfo objectInfo,
        CancellationToken cancellationToken)
    {
        switch (objectInfo.Kind)
        {
            case SqlObjectKind.Synonym:
                using (var command = CreateCommand(
                    connection,
                    SqlMetadataQueries.SynonymBase,
                    objectInfo.ObjectId))
                {
                    var value = command.ExecuteScalar();

                    return SqlCatalogScript.ForSynonym(objectInfo, value as string);
                }

            case SqlObjectKind.Sequence:
                var rows = ReadList(
                    connection,
                    SqlMetadataQueries.Sequence,
                    SqlMetadataReader.ReadSequence,
                    cancellationToken,
                    objectInfo.ObjectId);

                return SqlCatalogScript.ForSequence(objectInfo, rows.Count == 0 ? null : rows[0]);

            default:
                return null;
        }
    }

    /// <summary>
    /// 跑一次查詢，把每一列讀成 <typeparamref name="T"/>。
    /// </summary>
    /// <remarks>
    /// 每個查詢各自寫一次 command、parameter、reader 迴圈與取消檢查，漏掉哪一項
    /// 都不會編譯失敗：漏 timeout 是連線掛住、漏取消檢查是使用者換了資料庫
    /// 還在讀舊的、漏 dispose 是連線池被吃光。收成一個地方，新增查詢只剩一行。
    /// </remarks>
    /// <param name="objectId">要帶 @objectId 參數時的物件識別碼；查詢不吃參數時為 null。</param>
    private List<T> ReadList<T>(
        IDbConnection connection,
        string commandText,
        Func<IDataRecord, T> read,
        CancellationToken cancellationToken,
        int? objectId = null)
    {
        var items = new List<T>();
        using var command = CreateCommand(connection, commandText, objectId);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(read(reader));
        }

        return items;
    }

    /// <summary>
    /// 建立命令，並把查詢改寫成打到這個目錄的目標。
    /// </summary>
    /// <remarks>
    /// 限定字與參數的決定都收在這一處：跨伺服器時 <c>@objectId</c> 必須內嵌成常值
    /// （<c>OPENQUERY</c> 的內層是字串常值，參數傳不進去），漏掉的那一條查詢
    /// 會變成執行期的「必須宣告純量變數」，而 <c>TryLoad</c> 會把它降級成
    /// 「這一輪沒有資料」——症狀是那一層安靜地空掉。
    /// </remarks>
    private IDbCommand CreateCommand(IDbConnection connection, string commandText, int? objectId = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = _qualifier.Compose(commandText, objectId);
        command.CommandTimeout = _qualifier.IsRemote ? RemoteCommandTimeoutSeconds : _commandTimeoutSeconds;

        if (objectId is { } id && !_qualifier.IsRemote)
        {
            AddObjectIdParameter(command, id);
        }

        return command;
    }

    private static void AddObjectIdParameter(IDbCommand command, int objectId)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = SqlMetadataQueries.ObjectIdParameterName;
        parameter.DbType = DbType.Int32;
        parameter.Value = objectId;
        command.Parameters.Add(parameter);
    }
}
