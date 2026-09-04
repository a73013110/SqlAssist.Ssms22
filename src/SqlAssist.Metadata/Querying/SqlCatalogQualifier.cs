using System;
using System.Globalization;
using System.Text.RegularExpressions;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Metadata.Querying;

/// <summary>
/// 一份中繼資料目錄要打到哪裡去問。
/// </summary>
/// <remarks>
/// 同一台伺服器的別的資料庫是<b>換連線</b>（<see cref="SqlDatabaseScopedConnectionSource"/>
/// 開完之後 <c>ChangeDatabase</c>），SQL 一個字都不必改。連結伺服器換不了連線——
/// 那台伺服器不是我們連得上的——只能換 SQL 的限定字，因此多出這個型別。
///
/// 兩件事在這裡收成一份：
///
/// <list type="bullet">
/// <item><b>包成 <c>OPENQUERY</c> 而不是寫四段式名稱。</b>實測
/// <c>[srv].[db].sys.objects</c> 回傳的是 <c>sysrscols</c> 這一類內部系統資料表，
/// 不是使用者物件——那不是慢，是答錯。<c>OPENQUERY</c> 讓整句在對方執行，
/// 只有結果過線，順帶避開把遠端目錄檢視整份拉回來本機 JOIN
/// （<c>sys.columns</c> 那條有五個 JOIN）。</item>
/// <item><b><c>@objectId</c> 內嵌成常值。</b><c>OPENQUERY</c> 的內層是字串常值，
/// 參數傳不進去。型別是 <c>int</c>，沒有注入面，但格式必須是不變文化——
/// 跟著地區設定跑的話，某些地區會寫出帶群組分隔符號的數字而讓整句變成語法錯誤。</item>
/// </list>
/// </remarks>
public sealed class SqlCatalogQualifier
{
    /// <summary>目前這條連線；查詢原樣送出。</summary>
    public static readonly SqlCatalogQualifier Local = new(null, null);

    /// <summary>
    /// 目錄檢視的參考。查詢裡的 <c>sys.</c> 只出現在這個用途上，
    /// 字串常值裡的 <c>'sys'</c> 後面沒有點號，因此不會被誤換。
    /// </summary>
    private static readonly Regex CatalogViewReference = new(@"\bsys\.", RegexOptions.Compiled);

    private SqlCatalogQualifier(string? serverName, string? databaseName)
    {
        ServerName = serverName;
        DatabaseName = databaseName;
    }

    /// <summary>連結伺服器上的一個資料庫。</summary>
    public static SqlCatalogQualifier ForLinkedServer(string serverName, string databaseName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            throw new ArgumentException("伺服器名稱不可為空。", nameof(serverName));
        }

        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new ArgumentException("資料庫名稱不可為空。", nameof(databaseName));
        }

        return new SqlCatalogQualifier(serverName, databaseName);
    }

    /// <summary>
    /// 連結伺服器本身，還沒指定資料庫。
    /// </summary>
    /// <remarks>
    /// <c>LibMirror.</c> 這一格要的只有資料庫清單。物件與結構描述要再往右一格才問，
    /// 在這裡先撈一份等於對那台伺服器多送兩輪誰也不會看的查詢。
    /// </remarks>
    public static SqlCatalogQualifier ForLinkedServer(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            throw new ArgumentException("伺服器名稱不可為空。", nameof(serverName));
        }

        return new SqlCatalogQualifier(serverName, null);
    }

    public string? ServerName { get; }

    public string? DatabaseName { get; }

    /// <summary>要跨到別台伺服器才問得到。</summary>
    public bool IsRemote => ServerName is not null;

    /// <summary>只問得到資料庫清單。</summary>
    public bool IsServerRoot => IsRemote && DatabaseName is null;

    /// <summary>
    /// 把一條查詢改寫成打到這個目標的形狀。
    /// </summary>
    /// <param name="query">原始查詢，一律寫成不加限定的 <c>sys.</c>。</param>
    /// <param name="objectId">
    /// 查詢吃 <c>@objectId</c> 時的值；不吃參數時為 null。遠端會內嵌成常值，
    /// 本機仍走參數，由呼叫端依 <see cref="IsRemote"/> 決定要不要加上參數。
    /// </param>
    public string Compose(string query, int? objectId = null)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        // 本機與跨資料庫：決定查哪一個資料庫的是連線，不是 SQL。
        if (!IsRemote)
        {
            return query;
        }

        var inner = DatabaseName is null
            ? query
            : CatalogViewReference.Replace(query, SqlIdentifier.Quote(DatabaseName) + ".sys.");

        if (objectId is { } id)
        {
            inner = inner.Replace(
                SqlMetadataQueries.ObjectIdParameterName,
                id.ToString(CultureInfo.InvariantCulture));
        }

        return "SELECT * FROM OPENQUERY(" +
               SqlIdentifier.Quote(ServerName!) +
               ", '" +
               inner.Replace("'", "''") +
               "');";
    }
}
