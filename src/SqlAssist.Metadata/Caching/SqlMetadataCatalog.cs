using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
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

    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly object _detailLock = new();
    private readonly Dictionary<int, SqlObjectDetail> _details = new();
    private readonly Dictionary<int, SqlObjectStructure> _structures = new();
    private readonly ISqlConnectionSource _connectionSource;
    private readonly TimeSpan _lifetime;
    private readonly int _commandTimeoutSeconds;
    private SqlDatabaseSnapshot _snapshot = SqlDatabaseSnapshot.Empty;

    public SqlMetadataCatalog(
        ISqlConnectionSource connectionSource,
        TimeSpan lifetime,
        int commandTimeoutSeconds = 15)
    {
        _connectionSource = connectionSource ?? throw new ArgumentNullException(nameof(connectionSource));
        _lifetime = lifetime;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    public string CacheKey => _connectionSource.CacheKey;

    /// <summary>目前已快取的第一層資料；尚未載入時為空快照。呼叫端可用它先畫出清單。</summary>
    public SqlDatabaseSnapshot CachedSnapshot => Volatile.Read(ref _snapshot);

    /// <summary>清空所有層級的快取，下一次查詢會重新讀取資料庫。</summary>
    public void Invalidate()
    {
        Volatile.Write(ref _snapshot, SqlDatabaseSnapshot.Empty);

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

            var loaded = await Task.Run(() => LoadSnapshot(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _snapshot, loaded);
            return loaded;
        }
        finally
        {
            _snapshotGate.Release();
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
                Volatile.Write(ref _snapshot, LoadSnapshot(CancellationToken.None));
            }
            catch
            {
                // 背景更新失敗就繼續用舊資料，使用者不需要知道。
            }
            finally
            {
                _snapshotGate.Release();
            }
        });
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
    public async Task<SqlObjectDetail> GetDetailAsync(
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

        var detail = await Task.Run(() => LoadDetail(objectInfo, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

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
    public async Task<SqlObjectStructure> GetStructureAsync(
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

        var detail = await GetDetailAsync(objectInfo, cancellationToken).ConfigureAwait(false);
        var structure = objectInfo.Kind.HasColumns()
            ? await Task.Run(() => LoadStructure(detail, cancellationToken), cancellationToken)
                .ConfigureAwait(false)
            : new SqlObjectStructure(detail);

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

        var objects = ReadList(
                connection,
                SqlMetadataQueries.Objects,
                SqlMetadataReader.ReadObject,
                cancellationToken)
            .FindAll(info => info.Kind != SqlObjectKind.Unknown);

        var schemas = ReadList(
            connection,
            SqlMetadataQueries.Schemas,
            record => record.GetString(0),
            cancellationToken);

        return new SqlDatabaseSnapshot(
            _connectionSource.DatabaseName,
            objects,
            schemas,
            ReadDatabases(connection, cancellationToken),
            DateTimeOffset.UtcNow);
    }

    /// <remarks>
    /// 資料庫清單查不到不該讓整份快照失敗：權限不足時 sys.databases 仍會回傳
    /// 至少一列，但自訂的伺服器角色設定確實有可能整個擋掉。少了 USE 的建議
    /// 遠比整個物件清單都拿不到輕微。
    /// </remarks>
    private List<string> ReadDatabases(IDbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            return ReadList(
                connection,
                SqlMetadataQueries.Databases,
                record => record.GetString(0),
                cancellationToken);
        }
        catch (DbException)
        {
            return new List<string>();
        }
    }

    private SqlObjectDetail LoadDetail(SqlObjectInfo objectInfo, CancellationToken cancellationToken)
    {
        using var connection = _connectionSource.OpenConnection();
        var objectId = objectInfo.ObjectId;

        var columns = objectInfo.Kind.HasColumns()
            ? ReadList(
                connection,
                SqlMetadataQueries.Columns,
                SqlMetadataReader.ReadColumn,
                cancellationToken,
                objectId)
            : new List<SqlColumnInfo>();

        if (!objectInfo.Kind.IsModule())
        {
            return new SqlObjectDetail(objectInfo, columns, new List<SqlParameterInfo>(), definition: null);
        }

        var parameters = ReadList(
            connection,
            SqlMetadataQueries.Parameters,
            SqlMetadataReader.ReadParameter,
            cancellationToken,
            objectId);

        using var command = CreateCommand(connection, SqlMetadataQueries.Definition);
        AddObjectIdParameter(command, objectId);
        var value = command.ExecuteScalar();
        var definition = value is string text && !string.IsNullOrWhiteSpace(text) ? text : null;

        return new SqlObjectDetail(objectInfo, columns, parameters, definition);
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
        using var command = CreateCommand(connection, commandText);

        if (objectId is { } id)
        {
            AddObjectIdParameter(command, id);
        }

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(read(reader));
        }

        return items;
    }

    private IDbCommand CreateCommand(IDbConnection connection, string commandText)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = _commandTimeoutSeconds;
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
