using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>
/// 載入一個資料庫的資料字典。
/// </summary>
/// <remarks>
/// 連線來源由呼叫端在這一次呼叫裡借用，不留著（所有權在 <see cref="SqlMetadataCatalogRegistry"/>）。
/// 字典不進中繼資料目錄的快取：它只在使用者打開字典、選了一個資料庫時才要，
/// 常駐一份整個資料庫的資料行等於替每一個查詢視窗都付這份記憶體。
/// </remarks>
public static class SqlDataDictionaryLoader
{
    /// <summary>載入逾時；大資料庫的資料行有幾萬列，比清單查詢寬一些。</summary>
    public const int DefaultCommandTimeoutSeconds = 120;

    /// <summary>
    /// 載入字典；資料庫說不行時回 null，失敗帶著查詢名稱走 <see cref="SqlMetadataFailure"/>。
    /// </summary>
    /// <remarks>
    /// 回 null 而不是空字典：空字典是「這個資料庫沒有任何資料表」，該顯示空狀態；
    /// 問不到是另一件事，該說連不上或沒有權限。取消時丟 <see cref="OperationCanceledException"/>：
    /// 取消會讓伺服器回一個 <see cref="DbException"/>（使用者取消作業），那不是失敗，不該記一筆。
    /// </remarks>
    public static SqlDataDictionary? TryLoad(
        ISqlConnectionSource connectionSource,
        CancellationToken cancellationToken,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        if (connectionSource is null) throw new ArgumentNullException(nameof(connectionSource));

        try
        {
            using var connection = connectionSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = SqlDataDictionaryQueries.Load;
            command.CommandTimeout = commandTimeoutSeconds;

            using var cancel = cancellationToken.Register(command.Cancel);
            using var reader = command.ExecuteReader();
            return Read(reader, connectionSource.DatabaseName, cancellationToken);
        }
        catch (DbException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (DbException exception)
        {
            SqlMetadataFailure.Report("載入資料字典：" + connectionSource.DatabaseName, exception);
            return null;
        }
    }

    /// <summary>讀兩段結果；與連線無關的那一半，測試用假資料列餵得進來。</summary>
    public static SqlDataDictionary Read(IDataReader reader, string databaseName, CancellationToken cancellationToken)
    {
        if (reader is null) throw new ArgumentNullException(nameof(reader));

        var heads = new List<ObjectHead>();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            heads.Add(new ObjectHead(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                IsView(reader.GetString(3)) ? SqlDictionaryObjectKind.View : SqlDictionaryObjectKind.Table,
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        var columns = new Dictionary<int, List<SqlDictionaryColumn>>();

        if (reader.NextResult())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var objectId = reader.GetInt32(0);

                if (!columns.TryGetValue(objectId, out var list))
                {
                    columns[objectId] = list = new List<SqlDictionaryColumn>();
                }

                list.Add(ReadColumn(reader));
            }
        }

        var objects = new List<SqlDictionaryObject>(heads.Count);

        foreach (var head in heads)
        {
            objects.Add(new SqlDictionaryObject(
                head.ObjectId, head.SchemaName, head.Name, head.Kind, head.Description,
                columns.TryGetValue(head.ObjectId, out var list) ? list : (IReadOnlyList<SqlDictionaryColumn>)Array.Empty<SqlDictionaryColumn>()));
        }

        return new SqlDataDictionary(databaseName, objects);
    }

    private static SqlDictionaryColumn ReadColumn(IDataRecord record)
    {
        int? vectorDimensions = record.IsDBNull(12) ? null : record.GetInt32(12);
        var vectorBaseType = record.IsDBNull(13) ? null : record.GetString(13);

        return new SqlDictionaryColumn(
            record.GetInt32(1),
            record.GetString(2),
            SqlTypeFormatter.Format(record.GetString(3), record.GetInt16(4), record.GetByte(5), record.GetByte(6),
                vectorDimensions, vectorBaseType),
            record.GetBoolean(7),
            record.GetBoolean(10),
            record.GetBoolean(8),
            record.GetBoolean(9),
            record.IsDBNull(11) ? null : record.GetString(11));
    }

    /// <remarks><c>sys.objects.type</c> 是 <c>char(2)</c>，<c>V</c> 讀回來是 <c>"V "</c>。</remarks>
    private static bool IsView(string type) => type.Trim() == "V";

    private sealed class ObjectHead
    {
        public ObjectHead(int objectId, string schemaName, string name, SqlDictionaryObjectKind kind, string? description)
        {
            ObjectId = objectId;
            SchemaName = schemaName;
            Name = name;
            Kind = kind;
            Description = description;
        }

        public int ObjectId { get; }

        public string SchemaName { get; }

        public string Name { get; }

        public SqlDictionaryObjectKind Kind { get; }

        public string? Description { get; }
    }
}
