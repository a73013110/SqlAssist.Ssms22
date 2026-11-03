using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>
/// 匯出結構描述時才要的那幾樣：預設值、計算式、外部索引鍵參照與資料列數。
/// </summary>
/// <remarks>
/// 不併進 <see cref="SqlDataDictionaryQueries"/>：字典一打開就要載入，每多一欄就是每一個資料行多付一份，
/// 而這幾樣只有匯出那一刻用得到。照字典的規矩以集合為單位問、一輪來回，整個資料庫一次讀完，
/// 匯出哪幾張表由呼叫端挑——一百張表就一百輪的那一版，比整份讀回來在記憶體裡挑慢得多。
///
/// 沒有 <c>VIEW DEFINITION</c> 時 <c>definition</c> 讀回來是 NULL，那一格就空著，整份照樣匯出。
/// </remarks>
public sealed class SqlSchemaDetails
{
    /// <summary>四段一起送；欄位順序見各段。</summary>
    public const string Query = @"
SELECT dc.parent_object_id, dc.parent_column_id, dc.definition
FROM sys.default_constraints AS dc
INNER JOIN sys.objects AS o ON o.object_id = dc.parent_object_id
WHERE o.is_ms_shipped = 0;

SELECT cc.object_id, cc.column_id, cc.definition
FROM sys.computed_columns AS cc
INNER JOIN sys.objects AS o ON o.object_id = cc.object_id
WHERE o.is_ms_shipped = 0;

SELECT fkc.parent_object_id, fkc.parent_column_id, rs.name, ro.name, rc.name
FROM sys.foreign_key_columns AS fkc
INNER JOIN sys.objects AS po ON po.object_id = fkc.parent_object_id
INNER JOIN sys.objects AS ro ON ro.object_id = fkc.referenced_object_id
INNER JOIN sys.schemas AS rs ON rs.schema_id = ro.schema_id
INNER JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
WHERE po.is_ms_shipped = 0;

SELECT p.object_id, SUM(p.rows)
FROM sys.partitions AS p
INNER JOIN sys.objects AS o ON o.object_id = p.object_id
WHERE p.index_id IN (0, 1) AND o.type = 'U' AND o.is_ms_shipped = 0
GROUP BY p.object_id;";

    public const int DefaultCommandTimeoutSeconds = 120;

    private readonly Dictionary<(int, int), string> _defaults = new();
    private readonly Dictionary<(int, int), string> _computed = new();
    private readonly Dictionary<(int, int), string> _references = new();
    private readonly Dictionary<int, long> _rows = new();

    /// <summary>什麼都沒有的一份；讀不到細節時匯出照樣進行，那幾欄留空。</summary>
    public static SqlSchemaDetails Empty { get; } = new();

    public string? DefaultOf(int objectId, int columnId) => _defaults.TryGetValue((objectId, columnId), out var value) ? value : null;

    public string? ComputedOf(int objectId, int columnId) => _computed.TryGetValue((objectId, columnId), out var value) ? value : null;

    /// <summary>外部索引鍵指向的 <c>schema.table.column</c>；一個資料行參照好幾處時用逗號串起來。</summary>
    public string? ReferenceOf(int objectId, int columnId) => _references.TryGetValue((objectId, columnId), out var value) ? value : null;

    public long? RowsOf(int objectId) => _rows.TryGetValue(objectId, out var value) ? value : null;

    /// <summary>讀一份；資料庫說不行時回 null，失敗照字典的規矩記一筆。</summary>
    public static SqlSchemaDetails? TryLoad(ISqlConnectionSource connectionSource, CancellationToken cancellationToken,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        if (connectionSource is null) throw new ArgumentNullException(nameof(connectionSource));

        try
        {
            using var connection = connectionSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = Query;
            command.CommandTimeout = commandTimeoutSeconds;

            using var cancel = cancellationToken.Register(command.Cancel);
            using var reader = command.ExecuteReader();
            return Read(reader, cancellationToken);
        }
        catch (DbException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (DbException exception)
        {
            SqlMetadataFailure.Report("讀取結構描述細節：" + connectionSource.DatabaseName, exception);
            return null;
        }
    }

    /// <summary>讀四段結果；與連線無關的那一半，測試用假資料列餵得進來。</summary>
    public static SqlSchemaDetails Read(IDataReader reader, CancellationToken cancellationToken)
    {
        if (reader is null) throw new ArgumentNullException(nameof(reader));

        var details = new SqlSchemaDetails();
        ReadDefinitions(reader, details._defaults, cancellationToken);

        if (reader.NextResult()) ReadDefinitions(reader, details._computed, cancellationToken);

        if (reader.NextResult())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = (reader.GetInt32(0), reader.GetInt32(1));
                var target = reader.GetString(2) + "." + reader.GetString(3) + "." + reader.GetString(4);
                details._references[key] = details._references.TryGetValue(key, out var existing) ? existing + ", " + target : target;
            }
        }

        if (reader.NextResult())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                details._rows[reader.GetInt32(0)] = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return details;
    }

    private static void ReadDefinitions(IDataReader reader, Dictionary<(int, int), string> target, CancellationToken cancellationToken)
    {
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.IsDBNull(2)) continue;
            target[(reader.GetInt32(0), reader.GetInt32(1))] = reader.GetString(2);
        }
    }
}
