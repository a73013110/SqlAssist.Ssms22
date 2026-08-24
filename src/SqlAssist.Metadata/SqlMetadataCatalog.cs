using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Metadata;

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
        }
    }

    /// <summary>取得第一層資料；仍在有效期內時直接回傳快取。</summary>
    public async Task<SqlDatabaseSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (IsFresh(CachedSnapshot))
        {
            return CachedSnapshot;
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

    private bool IsFresh(SqlDatabaseSnapshot snapshot)
    {
        return !snapshot.IsEmpty && DateTimeOffset.UtcNow - snapshot.LoadedAt < _lifetime;
    }

    private SqlDatabaseSnapshot LoadSnapshot(CancellationToken cancellationToken)
    {
        using var connection = _connectionSource.OpenConnection();
        var objects = new List<SqlObjectInfo>();
        var schemas = new List<string>();

        using (var command = CreateCommand(connection, SqlMetadataQueries.Objects))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = SqlMetadataReader.ReadObject(reader);

                if (info.Kind != SqlObjectKind.Unknown)
                {
                    objects.Add(info);
                }
            }
        }

        using (var command = CreateCommand(connection, SqlMetadataQueries.Schemas))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                schemas.Add(reader.GetString(0));
            }
        }

        return new SqlDatabaseSnapshot(
            _connectionSource.DatabaseName,
            objects,
            schemas,
            DateTimeOffset.UtcNow);
    }

    private SqlObjectDetail LoadDetail(SqlObjectInfo objectInfo, CancellationToken cancellationToken)
    {
        using var connection = _connectionSource.OpenConnection();
        var columns = new List<SqlColumnInfo>();
        var parameters = new List<SqlParameterInfo>();
        string? definition = null;

        if (objectInfo.Kind.HasColumns())
        {
            using var command = CreateCommand(connection, SqlMetadataQueries.Columns);
            AddObjectIdParameter(command, objectInfo.ObjectId);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                columns.Add(SqlMetadataReader.ReadColumn(reader));
            }
        }

        if (objectInfo.Kind.IsModule())
        {
            using (var command = CreateCommand(connection, SqlMetadataQueries.Parameters))
            {
                AddObjectIdParameter(command, objectInfo.ObjectId);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    parameters.Add(SqlMetadataReader.ReadParameter(reader));
                }
            }

            using (var command = CreateCommand(connection, SqlMetadataQueries.Definition))
            {
                AddObjectIdParameter(command, objectInfo.ObjectId);
                var value = command.ExecuteScalar();
                definition = value is string text && !string.IsNullOrWhiteSpace(text) ? text : null;
            }
        }

        return new SqlObjectDetail(objectInfo, columns, parameters, definition);
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
