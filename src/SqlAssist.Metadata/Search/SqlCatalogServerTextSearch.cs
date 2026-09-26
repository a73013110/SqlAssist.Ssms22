using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Threading;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 伺服器端的本文比對：定義本文沒有留在記憶體裡的資料庫，每一輪用它找。
/// </summary>
/// <remarks>
/// 記憶體裡的那一份是快取，比對的完整度不能跟著它打折：本文超過單一資料庫上限、或快取把它的
/// 本文讓給別的資料庫時，這個資料庫仍然要搜得到。代價是每一輪一次伺服器端掃描，由取消控制
/// ——使用者多打一個字，查詢就被取消。
///
/// 伺服器只做粗篩（<see cref="SqlCatalogSearchQueries.DefinitionsMatching"/>），比對規則由
/// <see cref="SearchQuery.Matcher"/> 再比一次，所以大小寫、全字與記憶體那一條完全相同。
/// </remarks>
public static class SqlCatalogServerTextSearch
{
    /// <summary>
    /// 找出本文裡有這個字的物件，以及本文讀不到的物件；資料庫說不行時回傳 null。
    /// </summary>
    /// <remarks>
    /// 與索引那一層同一條降級規則：<see cref="DbException"/> 不冒出去。取消則<b>要</b>冒出去：
    /// 取消時伺服器回的也是 <see cref="DbException"/>（「使用者取消了作業」），降級成讀不到的話，
    /// 每打一個字就多一句「本文有一部分沒比到」。
    /// </remarks>
    [Localizable(false)]
    public static SqlCatalogServerTextMatches? TryFind(
        ISqlConnectionSource connectionSource,
        SearchQuery query,
        CancellationToken cancellationToken,
        int commandTimeoutSeconds = SqlCatalogSearchIndex.DefaultCommandTimeoutSeconds)
    {
        if (connectionSource is null) throw new ArgumentNullException(nameof(connectionSource));
        if (query is null) throw new ArgumentNullException(nameof(query));

        try
        {
            using var connection = connectionSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = SqlCatalogSearchQueries.DefinitionsMatching;
            command.CommandTimeout = commandTimeoutSeconds;
            AddParameter(command, SqlCatalogSearchQueries.ModifiedAfterParameterName, DbType.DateTime2, DBNull.Value);
            AddParameter(command, SqlCatalogSearchQueries.PatternParameterName, DbType.String, LikePattern(query.Text));

            using var cancel = cancellationToken.Register(command.Cancel);
            using var reader = command.ExecuteReader();

            var matches = new List<KeyValuePair<int, string>>();
            var unreadable = new List<int>();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var objectId = reader.GetInt32(0);

                if (reader.IsDBNull(1))
                {
                    unreadable.Add(objectId);
                    continue;
                }

                // 伺服器是粗篩；照使用者開的修飾再比一次，比不中的就不是命中。
                var definition = reader.GetString(1);
                if (query.Matcher.FindAll(definition, 0, 1).Count != 0)
                {
                    matches.Add(new KeyValuePair<int, string>(objectId, definition));
                }
            }

            return new SqlCatalogServerTextMatches(matches, unreadable);
        }
        catch (DbException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (DbException exception)
        {
            SqlMetadataFailure.Report("伺服器端比對定義本文：" + connectionSource.DatabaseName, exception);
            return null;
        }
    }

    /// <summary>
    /// 把使用者打的字變成 <c>LIKE</c> 的「含有」樣式；跳脫字元是 <c>\</c>。
    /// </summary>
    /// <remarks>
    /// <c>%</c>、<c>_</c>、<c>[</c> 都要跳脫：<c>sp_executesql</c> 的底線照原樣送出去會變成
    /// 「任一個字元」，粗篩雖然不會因此漏，但會多撈回一堆要在讀取端丟掉的本文。
    /// </remarks>
    internal static string LikePattern(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var pattern = new StringBuilder(text.Length + 8).Append('%');

        foreach (var ch in text)
        {
            if (ch is '\\' or '%' or '_' or '[') pattern.Append('\\');
            pattern.Append(ch);
        }

        return pattern.Append('%').ToString();
    }

    private static void AddParameter(IDbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;

        // nvarchar(max)：使用者打得出比四千字還長的字串時，截斷的樣式會比到錯的東西。
        if (type == DbType.String) parameter.Size = -1;

        command.Parameters.Add(parameter);
    }
}

/// <summary>伺服器端比對的結果：本文裡有這個字的物件，與本文讀不到的物件。</summary>
public sealed class SqlCatalogServerTextMatches
{
    internal SqlCatalogServerTextMatches(IReadOnlyList<KeyValuePair<int, string>> matches, IReadOnlyList<int> unreadable)
    {
        Matches = matches;
        Unreadable = unreadable;
    }

    /// <summary>object_id 與它的定義本文；已經照使用者的修飾比過。</summary>
    public IReadOnlyList<KeyValuePair<int, string>> Matches { get; }

    /// <summary>有模組而本文是 NULL 的 object_id（加密，或沒有 VIEW DEFINITION）。</summary>
    public IReadOnlyList<int> Unreadable { get; }
}
