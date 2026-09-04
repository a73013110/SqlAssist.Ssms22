using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>上一次載入失敗的時刻；沒有失敗過時為 0。</summary>
    private long _failedAtTicks;

    private readonly SemaphoreSlim _systemGate = new(1, 1);

    /// <summary>系統物件；只有真的被問到才載入，見 <see cref="GetSystemObjectsAsync"/>。</summary>
    private IReadOnlyList<SqlObjectInfo>? _systemObjects;

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

    /// <summary>目前已快取的第一層資料；尚未載入時為空快照。呼叫端可用它先畫出清單。</summary>
    public SqlDatabaseSnapshot CachedSnapshot => Volatile.Read(ref _snapshot);

    /// <summary>清空所有層級的快取，下一次查詢會重新讀取資料庫。</summary>
    public void Invalidate()
    {
        Volatile.Write(ref _snapshot, SqlDatabaseSnapshot.Empty);

        // 系統物件沒有有效期，只有這裡會把它丟掉——換連線就是換一台伺服器。
        Volatile.Write(ref _systemObjects, null);

        // 失敗退避一起清掉：按重新整理的人就是在說「我修好了，現在再試一次」。
        Volatile.Write(ref _failedAtTicks, 0);

        lock (_detailLock)
        {
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
            BeginBackgroundRefresh();
            return cached;
        }

        // 多個查詢視窗同時開啟時，只讓一條執行緒真的去查資料庫。
        await _snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (IsFresh(CachedSnapshot))
            {
                return CachedSnapshot;
            }

            var loaded = await Task
                .Run(() => TryLoad(() => LoadSnapshot(cancellationToken)), cancellationToken)
                .ConfigureAwait(false);

            if (loaded is null)
            {
                // 連不上就維持空快照，只記下失敗的時刻。把空的寫進快取會讓連線
                // 恢復之後仍然拿到空的，而完全不記則是下一次按鍵立刻再撞一次。
                RecordFailure();
                return SqlDatabaseSnapshot.Empty;
            }

            Volatile.Write(ref _snapshot, loaded);
            Volatile.Write(ref _failedAtTicks, 0);
            return loaded;
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

    private static T? TryLoad<T>(Func<T> load)
        where T : class
    {
        try
        {
            return load();
        }
        catch (DbException)
        {
            return null;
        }
    }

    /// <summary>在背景更新第一層資料；已經有人在更新時直接略過。</summary>
    private void BeginBackgroundRefresh()
    {
        if (!_snapshotGate.Wait(0))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                // 更新失敗就繼續用舊資料，使用者不需要知道；但仍要記下失敗，
                // 否則舊資料一過期就變成每一次按鍵排一次註定失敗的背景更新。
                if (TryLoad(() => LoadSnapshot(CancellationToken.None)) is { } loaded)
                {
                    Volatile.Write(ref _snapshot, loaded);
                    Volatile.Write(ref _failedAtTicks, 0);
                }
                else
                {
                    RecordFailure();
                }
            }
            catch
            {
                // 這是沒有人會接結果的背景工作，程式錯誤在這裡冒出去只會變成
                // 無人觀察的 Task 例外。Metadata 這一層沒有記錄器可用，
                // 只能讓它停在這裡；真正的錯誤會在下一次前景載入時原地重現。
            }
            finally
            {
                _snapshotGate.Release();
            }
        });
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
                .Run(() => TryLoad(() => LoadSystemObjects(cancellationToken)), cancellationToken)
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
    public async Task<SqlObjectDetail?> GetDetailAsync(
        SqlObjectInfo objectInfo,
        CancellationToken cancellationToken)
    {
        if (objectInfo is null)
        {
            throw new ArgumentNullException(nameof(objectInfo));
        }

        lock (_detailLock)
        {
            if (_details.TryGetValue(objectInfo.ObjectId, out var cached))
            {
                return cached;
            }
        }

        var detail = await Task
            .Run(() => TryLoad(() => LoadDetail(objectInfo, cancellationToken)), cancellationToken)
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
    /// <returns>資料庫取不到時為 <c>null</c>；理由見 <see cref="TryLoad{T}"/>。</returns>
    public async Task<SqlObjectStructure?> GetStructureAsync(
        SqlObjectInfo objectInfo,
        CancellationToken cancellationToken)
    {
        if (objectInfo is null)
        {
            throw new ArgumentNullException(nameof(objectInfo));
        }

        lock (_detailLock)
        {
            if (_structures.TryGetValue(objectInfo.ObjectId, out var cached))
            {
                return cached;
            }
        }

        if (await GetDetailAsync(objectInfo, cancellationToken).ConfigureAwait(false) is not { } detail)
        {
            return null;
        }

        // 索引與外來鍵只有本身就是一張資料表的那幾類查得出東西。資料表值函式
        // 這一輪也有資料行了，但它的指令碼來自定義本文，索引寫不進
        // CREATE FUNCTION——為它多跑一次第四層查詢，換不到任何顯示得出來的分頁。
        var structure = objectInfo.Kind.IsTableShaped()
            ? await Task
                .Run(() => TryLoad(() => LoadStructure(detail, cancellationToken)), cancellationToken)
                .ConfigureAwait(false)
            : new SqlObjectStructure(detail);

        if (structure is null)
        {
            return null;
        }

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

        return new SqlObjectStructure(
            detail,
            SqlIndexInfo.FromRows(indexRows),
            SqlForeignKeyInfo.FromRows(foreignKeyRows));
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
        return TryLoad(() => ReadList(
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
        return TryLoad(() => ReadList(
            connection,
            SqlMetadataQueries.LinkedServers,
            record => record.GetString(0),
            cancellationToken)) ?? new List<string>();
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

        if (!objectInfo.Kind.IsModule())
        {
            return new SqlObjectDetail(
                objectInfo,
                columns,
                new List<SqlParameterInfo>(),
                LoadSynthesizedDefinition(connection, objectInfo, cancellationToken));
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

        return new SqlObjectDetail(objectInfo, columns, parameters, definition);
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
