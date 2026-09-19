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
/// 一個資料庫的全量搜尋索引：物件、資料行、結構描述，以及（按需）定義本文。
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
/// <b>索引分兩段。</b>第一段（物件、資料行、結構描述）便宜而且一定要有；第二段
/// （<see cref="Definitions"/>）沒有上界，是第一次搜尋最貴的一段。這一輪不搜定義本文時
/// <see cref="Definitions"/> 是 null，第二條查詢連送都不送，記憶體也不佔。
/// 使用者之後改主意要搜本文時走 <see cref="TryAddDefinitions"/> 補上，
/// 第一段不必重掃。
///
/// <see cref="ModifiedThrough"/> 與 <see cref="ObjectCount"/> 是版本戳，
/// <see cref="TryRefresh"/> 拿它們只撈變更過的東西。兩個值必須與這一份索引同一次查詢
/// 算出來才對得起來，事後補不回去。
/// </remarks>
public sealed class SqlCatalogSearchIndex
{
    /// <summary>定義本文最多留這麼多位元組。</summary>
    /// <remarks>
    /// 真實資料庫裡單一個模組的定義動輒數 MB，整個資料庫加起來沒有上界。沒有這一條的
    /// 症狀不是慢，是搜尋一次就把幾百 MB 釘在 SSMS 的行程裡不放——而使用者按的只是
    /// 一次搜尋。超過之後只留名稱，並把 <see cref="SqlCatalogSearchDefinitions.IsComplete"/>
    /// 標成 false，讓呼叫端說得出「本文只掃到一部分」。安靜地少一半結果是最糟的：
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

    /// <summary>一個物件在索引上的固定開銷（實例、清單項目與字典項目）的粗估位元組。</summary>
    /// <remarks>
    /// 精確算不到也不必算：這個數字唯一的工作是讓快取的位元組預算跟著真實大小走，
    /// 而不是跟著「有幾個資料庫」走。寧可高估——低估的代價是預算擋不住，而那正是
    /// 位元組預算取代數量上限的理由。
    /// </remarks>
    private const int ObjectOverheadBytes = 128;

    private const int ColumnOverheadBytes = 64;

    private const string OpeningConnection = "開啟搜尋索引連線";
    private const string LoadingObjects = "載入搜尋索引物件";
    private const string LoadingColumns = "載入搜尋索引資料行";
    private const string LoadingDefinitions = "載入搜尋索引定義本文";
    private const string LoadingSchemas = "載入搜尋索引結構描述";

    private readonly Dictionary<int, SqlObjectInfo> _byObjectId;

    /// <param name="modifiedThrough">
    /// 這一份索引涵蓋到哪一刻的變更（<c>MAX(modify_date)</c>）；一個物件都沒有時為 null。
    /// </param>
    /// <param name="definitions">定義本文；這一份索引還沒撈第二段時為 null。</param>
    public SqlCatalogSearchIndex(
        string databaseName,
        IReadOnlyList<SqlObjectInfo> objects,
        IReadOnlyList<SqlCatalogSearchColumn> columns,
        IReadOnlyList<string> schemas,
        DateTime? modifiedThrough,
        SqlCatalogSearchDefinitions? definitions)
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
        Definitions = definitions;

        _byObjectId = new Dictionary<int, SqlObjectInfo>(objects.Count);
        var bytes = 0L;

        foreach (var info in objects)
        {
            // 同一個編號重複出現是資料有問題，不是這裡要救的事；後到的覆蓋前一個即可。
            _byObjectId[info.ObjectId] = info;
            bytes += ObjectOverheadBytes + (long)(info.SchemaName.Length + info.Name.Length) * sizeof(char);
        }

        foreach (var column in columns)
        {
            bytes += ColumnOverheadBytes + (long)column.Name.Length * sizeof(char);
        }

        foreach (var schema in schemas)
        {
            bytes += ColumnOverheadBytes + (long)schema.Length * sizeof(char);
        }

        ApproximateBytes = bytes + (definitions?.Bytes ?? 0);
    }

    /// <summary>這一份索引是哪一個資料庫的。</summary>
    public string DatabaseName { get; }

    public IReadOnlyList<SqlObjectInfo> Objects { get; }

    public IReadOnlyList<SqlCatalogSearchColumn> Columns { get; }

    /// <summary>
    /// 結構描述名稱。
    /// </summary>
    /// <remarks>
    /// 不回報成搜尋結果——分類表上沒有「結構描述」這一種，而回報一個使用者勾不掉
    /// 的分類等於一組永遠過濾不掉的列。仍然在這裡撈回來，是因為它與物件、資料行走的是
    /// 同一條連線、同一次往返，事後要補就得再開一次連線；之後要做「只搜某個結構描述」
    /// 的限定字過濾，要的就是這一份。
    /// </remarks>
    public IReadOnlyList<string> Schemas { get; }

    /// <summary>定義本文；這一份索引還沒撈第二段時為 null。</summary>
    /// <remarks>
    /// null 與 <see cref="SqlCatalogSearchDefinitions.Empty"/> 是兩件事：前者是「還沒問」，
    /// 後者是「問過了，這個資料庫真的一份定義本文都沒有」。混成一件的症狀是每一輪搜尋
    /// 都對沒有模組的資料庫再發一次第二段查詢。
    /// </remarks>
    public SqlCatalogSearchDefinitions? Definitions { get; }

    /// <summary>這一份索引涵蓋到哪一刻的變更；空索引為 null。</summary>
    public DateTime? ModifiedThrough { get; }

    /// <summary>索引到幾個物件；與 <see cref="ModifiedThrough"/> 一起當版本戳。</summary>
    /// <remarks>
    /// 只看 <c>MAX(modify_date)</c> 分不出「什麼都沒變」與「剛好卸除了最後改過的那一個」
    /// ——後者的最大值會倒退，而倒退看起來與沒變一樣。物件數是第二個維度。
    /// </remarks>
    public int ObjectCount => Objects.Count;

    /// <summary>
    /// 這一份索引大約佔多少位元組；快取的位元組預算靠它淘汰。
    /// </summary>
    /// <remarks>
    /// 照數量算上限（「同時留四份」）的症狀是四個大庫就把行程撐爆，而四個小庫又浪費了
    /// 明明留得住的東西——一份索引的大小差到三個數量級，數量與記憶體之間沒有關係。
    /// </remarks>
    public long ApproximateBytes { get; }

    /// <summary>這個編號在這一份索引裡。</summary>
    internal bool Contains(int objectId) => _byObjectId.ContainsKey(objectId);

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
    /// 所有查詢走<b>同一條連線</b>：它們一定是一起要的，分開等於每建一份索引多開幾次連線。
    /// 回傳 null 的那一輪什麼都不留給呼叫端快取——失敗進了快取，連線恢復之後仍然是空的。
    /// </remarks>
    /// <param name="includeDefinitions">
    /// 要不要撈第二段。false 時<b>不送</b>那一條查詢，也不佔記憶體——這就是
    /// <see cref="Core.Search.SearchTargets"/> 少掉 <c>Text</c> 會省下的東西。
    /// </param>
    public static SqlCatalogSearchIndex? TryBuild(
        ISqlConnectionSource connectionSource,
        bool includeDefinitions,
        CancellationToken cancellationToken,
        long maxDefinitionBytes = DefaultMaxDefinitionBytes,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds) =>
        Load(connectionSource, previous: null, includeDefinitions, cancellationToken, maxDefinitionBytes,
            commandTimeoutSeconds);

    /// <summary>
    /// 只補第二段：第一段原樣沿用，回傳一份新的索引。
    /// </summary>
    /// <remarks>
    /// 使用者先只搜名稱、之後才勾上定義本文時走這一條。整份重建的話，那個「勾一下」要付的是
    /// 一次完整的全表掃描，而物件與資料行那兩份手上明明就有。
    ///
    /// 不就地改寫自己：同一份索引會被好幾條搜尋執行緒同時讀。
    /// </remarks>
    public SqlCatalogSearchIndex? TryAddDefinitions(
        ISqlConnectionSource connectionSource,
        CancellationToken cancellationToken,
        long maxDefinitionBytes = DefaultMaxDefinitionBytes,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        if (connectionSource is null)
        {
            throw new ArgumentNullException(nameof(connectionSource));
        }

        if (Definitions is not null)
        {
            return this;
        }

        var operation = OpeningConnection;

        try
        {
            using var connection = connectionSource.OpenConnection();

            operation = LoadingDefinitions;
            var definitions = ReadDefinitions(
                connection, _byObjectId, previous: null, reusable: null, modifiedAfter: null,
                maxDefinitionBytes, commandTimeoutSeconds, cancellationToken);

            return new SqlCatalogSearchIndex(
                DatabaseName, Objects, Columns, Schemas, ModifiedThrough, definitions);
        }
        catch (DbException exception)
        {
            SqlMetadataFailure.Report(operation + "：" + DatabaseName, exception);
            return null;
        }
    }

    /// <summary>
    /// 沿著版本戳重新整理：只撈變更過的物件，其餘沿用上一份。
    /// </summary>
    /// <remarks>
    /// 物件那一條<b>整份重撈</b>——它是唯一看得出「哪一個被卸除了」的一條，而卸除不會留下
    /// 時間戳；它同時也是三條裡最便宜的。貴的兩條（資料行、定義本文）才走
    /// <see cref="SqlCatalogSearchQueries.ModifiedAfterParameterName"/>。
    ///
    /// 只要有一個物件說不出自己的 <c>modify_date</c>，或出現一個上一輪沒有、時間戳卻比
    /// 界線還舊的物件，就整份重撈：增量查詢撈不到那幾個，而沿用上一份等於它們的資料行與
    /// 定義本文永遠不出現，畫面上看不出少了什麼。寧可多付一次。
    /// </remarks>
    public static SqlCatalogSearchIndex? TryRefresh(
        SqlCatalogSearchIndex previous,
        ISqlConnectionSource connectionSource,
        bool includeDefinitions,
        CancellationToken cancellationToken,
        long maxDefinitionBytes = DefaultMaxDefinitionBytes,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        if (previous is null)
        {
            throw new ArgumentNullException(nameof(previous));
        }

        return Load(connectionSource, previous, includeDefinitions, cancellationToken, maxDefinitionBytes,
            commandTimeoutSeconds);
    }

    private static SqlCatalogSearchIndex? Load(
        ISqlConnectionSource connectionSource,
        SqlCatalogSearchIndex? previous,
        bool includeDefinitions,
        CancellationToken cancellationToken,
        long maxDefinitionBytes,
        int commandTimeoutSeconds)
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
            var rows = ReadObjects(
                connection, databaseName, commandTimeoutSeconds, cancellationToken, out var modifiedThrough);

            var objects = new List<SqlObjectInfo>(rows.Count);
            var byObjectId = new Dictionary<int, SqlObjectInfo>(rows.Count);

            foreach (var row in rows)
            {
                objects.Add(row.Info);
                byObjectId[row.Info.ObjectId] = row.Info;
            }

            var reusable = previous?.ModifiedThrough is { } stamp ? Reusable(rows, previous, stamp) : null;
            var modifiedAfter = reusable is null ? (DateTime?)null : previous!.ModifiedThrough;

            operation = LoadingColumns;
            var columns = ReadColumns(
                connection, byObjectId, modifiedAfter, commandTimeoutSeconds, cancellationToken);

            if (reusable is not null)
            {
                columns = MergeColumns(previous!.Columns, columns, reusable);
            }

            SqlCatalogSearchDefinitions? definitions = null;

            if (includeDefinitions)
            {
                operation = LoadingDefinitions;

                // 上一份沒有第二段時，這一輪的第二段要整份撈——沿用的界線只對「上一份已經
                // 有而且沒變更過」的那幾份成立。
                var reusableDefinitions = previous?.Definitions is null ? null : reusable;

                definitions = ReadDefinitions(
                    connection, byObjectId, previous, reusableDefinitions,
                    reusableDefinitions is null ? null : modifiedAfter,
                    maxDefinitionBytes, commandTimeoutSeconds, cancellationToken);
            }

            operation = LoadingSchemas;
            var schemas = ReadSchemas(connection, commandTimeoutSeconds, cancellationToken);

            return new SqlCatalogSearchIndex(
                databaseName, objects, columns, schemas, modifiedThrough, definitions);
        }
        catch (DbException exception)
        {
            SqlMetadataFailure.Report(operation + "：" + databaseName, exception);
            return null;
        }
    }

    /// <summary>
    /// 哪幾個物件的資料行與定義本文可以沿用上一份；整份重撈時回傳 null。
    /// </summary>
    /// <remarks>
    /// 界線比對寫成 <c>&gt;=</c>，與查詢那一端一致；理由見
    /// <see cref="SqlCatalogSearchQueries.ModifiedAfterParameterName"/>。
    /// </remarks>
    private static HashSet<int>? Reusable(List<ObjectRow> rows, SqlCatalogSearchIndex previous, DateTime stamp)
    {
        var reusable = new HashSet<int>();

        foreach (var row in rows)
        {
            // 沒有時間戳就分不出變沒變，而增量查詢也撈不到它。
            if (row.ModifiedAt is not { } modifiedAt) return null;

            // 這一輪會重撈它，不必沿用。
            if (modifiedAt >= stamp) continue;

            // 上一輪沒有、時間戳卻比界線舊：增量查詢不會回傳它，沿用也沒有東西可沿用。
            if (!previous.Contains(row.Info.ObjectId)) return null;

            reusable.Add(row.Info.ObjectId);
        }

        return reusable;
    }

    /// <remarks>
    /// 沿用的資料行連同它那一份 <see cref="SqlObjectInfo"/> 一起留下，不改指向這一輪的實例：
    /// 「沒有變更過」同時也表示名稱與結構描述沒有動過（<c>ALTER</c> 與 <c>sp_rename</c>
    /// 都會推進 <c>modify_date</c>），兩份的內容一樣。重新配一份只是為了好看而多配置一次。
    ///
    /// 順序是「沿用的在前、重撈的在後」，不是 <c>object_id</c> 順序。同一份上一輪的索引
    /// 每次重新整理排出來的順序仍然一樣，而排名是聚合器的事——這裡只要可重現。
    /// </remarks>
    private static List<SqlCatalogSearchColumn> MergeColumns(
        IReadOnlyList<SqlCatalogSearchColumn> previous,
        List<SqlCatalogSearchColumn> fresh,
        HashSet<int> reusable)
    {
        var merged = new List<SqlCatalogSearchColumn>(previous.Count + fresh.Count);

        foreach (var column in previous)
        {
            if (reusable.Contains(column.Owner.ObjectId)) merged.Add(column);
        }

        merged.AddRange(fresh);
        return merged;
    }

    /// <remarks>
    /// 認不得的型別代碼整筆丟掉（<see cref="SqlObjectKind.Unknown"/>）：它沒有分類可以掛，
    /// 而回報一筆掛在「不知道是什麼」上的結果，使用者點下去也沒有東西可以打開。
    /// 這與第一層快照對未知種類的處置一致。
    /// </remarks>
    private static List<ObjectRow> ReadObjects(
        IDbConnection connection,
        string databaseName,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken,
        out DateTime? modifiedThrough)
    {
        var rows = new List<ObjectRow>();
        DateTime? stamp = null;

        using (var command = CreateCommand(connection, SqlCatalogSearchQueries.Objects, commandTimeoutSeconds))
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

                DateTime? modifiedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4);

                if (modifiedAt is { } at && (stamp is null || at > stamp.Value))
                {
                    stamp = at;
                }

                rows.Add(new ObjectRow(info, modifiedAt));
            }
        }

        modifiedThrough = stamp;
        return rows;
    }

    /// <remarks>
    /// 對不上物件清單的資料行直接跳過：那是在兩條查詢之間剛被建立的東西，或種類認不得
    /// 因而沒有進索引的物件。掛一筆「不知道屬於誰」的資料行上去，畫面上會出現一列沒有位置的結果。
    /// </remarks>
    private static List<SqlCatalogSearchColumn> ReadColumns(
        IDbConnection connection,
        Dictionary<int, SqlObjectInfo> byObjectId,
        DateTime? modifiedAfter,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var columns = new List<SqlCatalogSearchColumn>();

        if (byObjectId.Count == 0)
        {
            return columns;
        }

        using var command = CreateCommand(
            connection, SqlCatalogSearchQueries.Columns, commandTimeoutSeconds, modifiedAfter);
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

    /// <param name="reusable">
    /// 可以沿用上一份的那幾個編號；null 表示整份重撈。
    /// </param>
    private static SqlCatalogSearchDefinitions ReadDefinitions(
        IDbConnection connection,
        Dictionary<int, SqlObjectInfo> byObjectId,
        SqlCatalogSearchIndex? previous,
        HashSet<int>? reusable,
        DateTime? modifiedAfter,
        long maxDefinitionBytes,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var builder = new SqlCatalogSearchDefinitions.Builder(maxDefinitionBytes);
        var inheritIncomplete = false;

        // 先沿用再收新的：位元組上限用盡時被丟掉的是這一輪剛撈回來的那幾份，
        // 而它們至少還說得出「不完整」。反過來的話，上一輪已經有的本文會無聲消失。
        if (reusable is not null && previous?.Definitions is { } kept)
        {
            inheritIncomplete = !kept.IsComplete;

            foreach (var objectId in reusable)
            {
                builder.Reuse(kept, objectId);
            }
        }

        using var command = CreateCommand(
            connection, SqlCatalogSearchQueries.Definitions, commandTimeoutSeconds, modifiedAfter);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.IsDBNull(1))
            {
                continue;
            }

            var objectId = reader.GetInt32(0);

            // 種類認不得而沒有進索引的物件，它的定義本文也不必留：沒有地方可以掛那一筆命中。
            if (byObjectId.ContainsKey(objectId))
            {
                builder.Add(objectId, reader.GetString(1));
            }
        }

        return builder.Build(inheritIncomplete);
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

    /// <remarks>
    /// 有參數的查詢一律在這裡綁值，不留給呼叫端：漏綁的症狀是執行期的「必須宣告純量變數」，
    /// 而那是 <see cref="DbException"/>，會被降級成「這一輪沒有資料」——搜尋對那個資料庫
    /// 安靜地空掉。型別明著指定成 <see cref="DbType.DateTime2"/>：傳 NULL 時推不出型別，
    /// 而 <c>modify_date</c> 是 <c>datetime</c>，比較時由伺服器放大，不會失去精確度。
    /// </remarks>
    private static IDbCommand CreateCommand(
        IDbConnection connection, string commandText, int commandTimeoutSeconds, DateTime? modifiedAfter = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = commandTimeoutSeconds;

        if (commandText.IndexOf(SqlCatalogSearchQueries.ModifiedAfterParameterName, StringComparison.Ordinal) < 0)
        {
            return command;
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = SqlCatalogSearchQueries.ModifiedAfterParameterName;
        parameter.DbType = DbType.DateTime2;
        parameter.Value = modifiedAfter.HasValue ? modifiedAfter.Value : (object)DBNull.Value;
        command.Parameters.Add(parameter);
        return command;
    }

    /// <summary>物件查詢的一列：識別欄位加上它自己的版本戳。</summary>
    /// <remarks>
    /// 時間戳不進 <see cref="SqlObjectInfo"/>：那個型別在按鍵路徑上被建立成千上萬次
    /// （第一層快照），多一個欄位就是每一個物件多八個位元組，而讀它的四條路徑一個都用不到。
    /// 建索引的這一輪用完就丟。
    /// </remarks>
    private readonly struct ObjectRow
    {
        internal ObjectRow(SqlObjectInfo info, DateTime? modifiedAt)
        {
            Info = info;
            ModifiedAt = modifiedAt;
        }

        internal SqlObjectInfo Info { get; }

        internal DateTime? ModifiedAt { get; }
    }
}
