using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 一個資料庫的全量搜尋索引：物件、資料行、結構描述與定義本文。
/// </summary>
/// <remarks>
/// <b>與 <see cref="SqlMetadataCatalog"/> 完全分離，而且刻意不重用它。</b>那四層是<b>按需</b>
/// 載入的——第三層的定義本文只在要顯示某一個物件時才撈一份，第二層的資料行只在使用者
/// 選了某一張表之後才問。搜尋要的正好相反：一次要全部。兩者併在一起的話，失效策略會
/// 互相打架（搜尋一次就把整個資料庫的定義本文灌進按鍵路徑上的常駐快取，
/// 而按鍵路徑的過期時間是為了「名稱清單別太舊」訂的），而且每一次搜尋都會讓
/// 建議清單的快取被自己的資料擠掉。
///
/// 連線與快取鍵則<b>必須</b>共用：連線走 <see cref="ISqlConnectionSource"/>，
/// 鍵走 <see cref="SqlConnectionCacheKey"/>。自己拼一份字串當鍵的症狀是同一個資料庫
/// 拿到兩份索引——查詢次數加倍，而兩份的新舊各走各的。
///
/// v1 <b>整份重建</b>。<see cref="ModifiedThrough"/> 與 <see cref="ObjectCount"/> 現在沒有人讀，
/// 仍然記著：增量更新要的就是這兩個值（多一個
/// <c>WHERE modify_date &gt; @stamp</c> 加上「物件數變了沒」），而它們必須與這一份索引
/// 同一次查詢算出來才對得起來，事後補不回去。
/// </remarks>
public sealed class SqlCatalogSearchIndex
{
    /// <summary>定義本文最多留這麼多位元組。</summary>
    /// <remarks>
    /// 真實資料庫裡單一個模組的定義動輒數 MB，整個資料庫加起來沒有上界。沒有這一條的
    /// 症狀不是慢，是搜尋一次就把幾百 MB 釘在 SSMS 的行程裡不放——而使用者按的只是
    /// 一次搜尋。超過之後只留名稱，並把 <see cref="HasCompleteDefinitions"/> 標成 false，
    /// 讓呼叫端說得出「本文只掃到一部分」。安靜地少一半結果是最糟的：
    /// 使用者會以為那個字串在這個資料庫裡不存在。
    /// </remarks>
    public const long DefaultMaxDefinitionBytes = 64L * 1024 * 1024;

    /// <summary>建索引的命令逾時；比按鍵路徑寬。</summary>
    /// <remarks>
    /// 這一輪掃的是整個資料庫，不在按鍵路徑上——用第一層那種秒級逾時的話，
    /// 大一點的資料庫每一次都會逾時，而逾時是 <see cref="DbException"/>，
    /// 會被降級成「這一輪沒有這個資料庫的資料」，症狀是搜尋對大資料庫永遠空白。
    /// </remarks>
    public const int DefaultCommandTimeoutSeconds = 60;

    private static readonly SqlCatalogSearchObject[] NoObjects = Array.Empty<SqlCatalogSearchObject>();
    private static readonly SqlCatalogSearchColumn[] NoColumns = Array.Empty<SqlCatalogSearchColumn>();

    /// <param name="modifiedThrough">
    /// 這一份索引涵蓋到哪一刻的變更（<c>MAX(modify_date)</c>）；一個物件都沒有時為 null。
    /// </param>
    /// <param name="hasCompleteDefinitions">
    /// 定義本文有沒有全部收進來；false 表示位元組上限用盡，後面的物件只有名稱。
    /// </param>
    public SqlCatalogSearchIndex(
        string databaseName,
        IReadOnlyList<SqlCatalogSearchObject> objects,
        IReadOnlyList<SqlCatalogSearchColumn> columns,
        IReadOnlyList<string> schemas,
        DateTime? modifiedThrough,
        bool hasCompleteDefinitions)
    {
        if (string.IsNullOrEmpty(databaseName))
        {
            throw new ArgumentException("資料庫名稱不可為空。", nameof(databaseName));
        }

        DatabaseName = databaseName;
        Objects = objects ?? throw new ArgumentNullException(nameof(objects));
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        Schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
        ModifiedThrough = modifiedThrough;
        HasCompleteDefinitions = hasCompleteDefinitions;
    }

    /// <summary>這一份索引是哪一個資料庫的。</summary>
    public string DatabaseName { get; }

    public IReadOnlyList<SqlCatalogSearchObject> Objects { get; }

    public IReadOnlyList<SqlCatalogSearchColumn> Columns { get; }

    /// <summary>
    /// 結構描述名稱。
    /// </summary>
    /// <remarks>
    /// v1 不回報成搜尋結果——分類表上沒有「結構描述」這一種，而回報一個使用者勾不掉
    /// 的分類等於一組永遠過濾不掉的列。仍然在這裡撈回來，是因為它與物件、資料行走的是
    /// 同一條連線、同一次往返，事後要補就得再開一次連線；之後要做「只搜某個結構描述」
    /// 的限定字過濾，要的就是這一份。
    /// </remarks>
    public IReadOnlyList<string> Schemas { get; }

    /// <summary>這一份索引涵蓋到哪一刻的變更；空索引為 null。</summary>
    public DateTime? ModifiedThrough { get; }

    /// <summary>索引到幾個物件；與 <see cref="ModifiedThrough"/> 一起當版本戳。</summary>
    /// <remarks>
    /// 只看 <c>MAX(modify_date)</c> 分不出「什麼都沒變」與「剛好卸除了最後改過的那一個」
    /// ——後者的最大值會倒退，而倒退看起來與沒變一樣。物件數是第二個維度。
    /// </remarks>
    public int ObjectCount => Objects.Count;

    /// <summary>
    /// 定義本文完整嗎；false 表示位元組上限用盡，只剩名稱。
    /// </summary>
    public bool HasCompleteDefinitions { get; }

    /// <summary>
    /// 對一個資料庫建一份索引；資料庫說不行時回傳 null。
    /// </summary>
    /// <remarks>
    /// <b>不讓 <see cref="DbException"/> 冒出去。</b>連不上、逾時、權限不足一律降級成
    /// 「這一輪沒有這個資料庫的資料」，理由與 <see cref="SqlMetadataCatalog"/> 那一條一樣：
    /// 冒出去會落在 Ssms22 的平台邊界上，而它把每一次都記成一份完整堆疊——
    /// 連線斷掉時使用者每打一個字就失敗一次，真正的程式錯誤會被埋掉。
    ///
    /// 只接 <see cref="DbException"/>。參數契約違反與其餘任何例外都是程式錯誤，
    /// 該一路浮到邊界去留下完整堆疊。
    ///
    /// 降級不等於一個字都不留：<see cref="SqlMetadataFailure"/> 帶著「哪一條查詢」
    /// 與伺服器說的那句話走。少了它，「連線斷了」與「這條查詢寫錯了」在畫面上
    /// 長得一模一樣。
    ///
    /// 三條查詢走<b>同一條連線</b>：它們一定是一起要的，分開等於每建一份索引多開兩次連線。
    /// 回傳 null 的那一輪什麼都不留給呼叫端快取——失敗進了快取，連線恢復之後仍然是空的。
    /// </remarks>
    public static SqlCatalogSearchIndex? TryBuild(
        ISqlConnectionSource connectionSource,
        CancellationToken cancellationToken,
        long maxDefinitionBytes = DefaultMaxDefinitionBytes,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        if (connectionSource is null)
        {
            throw new ArgumentNullException(nameof(connectionSource));
        }

        if (maxDefinitionBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDefinitionBytes));
        }

        var databaseName = connectionSource.DatabaseName;

        // 失敗訊息要指得出是哪一步。一路更新這個區域變數，比每一條查詢各包一個
        // try/catch 少三層縮排，而且不會有哪一條忘了包。
        var operation = OpeningConnection;

        try
        {
            using var connection = connectionSource.OpenConnection();

            operation = LoadingObjects;
            var objects = ReadObjects(
                connection, databaseName, maxDefinitionBytes, commandTimeoutSeconds, cancellationToken,
                out var modifiedThrough, out var hasCompleteDefinitions);

            operation = LoadingColumns;
            var columns = ReadColumns(connection, objects, commandTimeoutSeconds, cancellationToken);

            operation = LoadingSchemas;
            var schemas = ReadSchemas(connection, commandTimeoutSeconds, cancellationToken);

            return new SqlCatalogSearchIndex(
                databaseName, objects, columns, schemas, modifiedThrough, hasCompleteDefinitions);
        }
        catch (DbException exception)
        {
            SqlMetadataFailure.Report(operation + "：" + databaseName, exception);
            return null;
        }
    }

    private const string OpeningConnection = "開啟搜尋索引連線";
    private const string LoadingObjects = "載入搜尋索引物件";
    private const string LoadingColumns = "載入搜尋索引資料行";
    private const string LoadingSchemas = "載入搜尋索引結構描述";

    /// <remarks>
    /// 認不得的型別代碼整筆丟掉（<see cref="SqlObjectKind.Unknown"/>）：它沒有分類可以掛，
    /// 而回報一筆掛在「不知道是什麼」上的結果，使用者點下去也沒有東西可以打開。
    /// 這與第一層快照對未知種類的處置一致。
    /// </remarks>
    private static List<SqlCatalogSearchObject> ReadObjects(
        IDbConnection connection,
        string databaseName,
        long maxDefinitionBytes,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken,
        out DateTime? modifiedThrough,
        out bool hasCompleteDefinitions)
    {
        var objects = new List<SqlCatalogSearchObject>();
        var definitionBytes = 0L;
        DateTime? stamp = null;
        hasCompleteDefinitions = true;

        using (var command = CreateCommand(
                   connection, SqlCatalogSearchQueries.ObjectsWithDefinitions, commandTimeoutSeconds))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 前四欄與第一層的物件查詢一致，對應直接共用——「哪一欄是什麼」
                // 只寫一份，加欄位時不會有一邊忘了改。
                var info = SqlMetadataReader.ReadObject(reader, databaseName);

                if (info.Kind == SqlObjectKind.Unknown)
                {
                    continue;
                }

                if (!reader.IsDBNull(4))
                {
                    var modifiedAt = reader.GetDateTime(4);
                    if (stamp is null || modifiedAt > stamp.Value)
                    {
                        stamp = modifiedAt;
                    }
                }

                objects.Add(new SqlCatalogSearchObject(
                    info,
                    ReadDefinition(reader, maxDefinitionBytes, ref definitionBytes, ref hasCompleteDefinitions)));
            }
        }

        modifiedThrough = stamp;
        return objects;
    }

    /// <summary>
    /// 讀一份定義本文；位元組上限用盡之後只回 null 並標記索引不完整。
    /// </summary>
    /// <remarks>
    /// 上限算的是<b>留下來</b>的位元組。已經串流回來的那一列還是得讀過去（不讀就沒有
    /// 下一列），差別在於不把字串留在索引上，讓它當場可以回收。UTF-16 一個字元兩個
    /// 位元組，所以用 <c>Length * 2</c> 算——照字元數算的話上限會與實際佔用差一倍，
    /// 而「64 MiB」這種數字寫在設定上是要對得起來的。
    ///
    /// 上限用盡之後<b>不提前跳出迴圈</b>：名稱還要繼續收。只有本文停。
    /// </remarks>
    private static string? ReadDefinition(
        IDataRecord record, long maxDefinitionBytes, ref long definitionBytes, ref bool hasCompleteDefinitions)
    {
        if (record.IsDBNull(5))
        {
            return null;
        }

        var definition = record.GetString(5);
        var cost = (long)definition.Length * sizeof(char);

        if (definitionBytes + cost > maxDefinitionBytes)
        {
            hasCompleteDefinitions = false;
            return null;
        }

        definitionBytes += cost;
        return definition;
    }

    /// <remarks>
    /// 對不上物件清單的資料行直接跳過：那是系統內部物件，或是在兩條查詢之間剛被建立的
    /// 東西。掛一筆「不知道屬於誰」的資料行上去，畫面上會出現一列沒有位置的結果。
    /// </remarks>
    private static List<SqlCatalogSearchColumn> ReadColumns(
        IDbConnection connection,
        List<SqlCatalogSearchObject> objects,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var columns = new List<SqlCatalogSearchColumn>();

        if (objects.Count == 0)
        {
            return columns;
        }

        var byObjectId = new Dictionary<int, SqlObjectInfo>(objects.Count);

        foreach (var entry in objects)
        {
            // 同一個編號重複出現是資料有問題，不是這裡要救的事；後到的覆蓋前一個即可。
            byObjectId[entry.Info.ObjectId] = entry.Info;
        }

        using var command = CreateCommand(connection, SqlCatalogSearchQueries.Columns, commandTimeoutSeconds);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (byObjectId.TryGetValue(reader.GetInt32(0), out var owner))
            {
                columns.Add(new SqlCatalogSearchColumn(owner, reader.GetString(1)));
            }
        }

        return columns;
    }

    private static List<string> ReadSchemas(
        IDbConnection connection, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        var schemas = new List<string>();

        using var command = CreateCommand(connection, SqlMetadataQueries.Schemas, commandTimeoutSeconds);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            schemas.Add(reader.GetString(0));
        }

        return schemas;
    }

    private static IDbCommand CreateCommand(IDbConnection connection, string commandText, int commandTimeoutSeconds)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = commandTimeoutSeconds;
        return command;
    }
}
