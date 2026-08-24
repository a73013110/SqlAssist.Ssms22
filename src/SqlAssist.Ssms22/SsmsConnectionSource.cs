using System;
using System.Data;
using SqlAssist.Metadata;

namespace SqlAssist.Ssms22;

/// <summary>
/// 以 SSMS 查詢視窗目前的連線為樣板，供中繼資料查詢另外開連線。
/// </summary>
/// <remarks>
/// 建構時（在 UI 執行緒上、握有 SSMS 的實際連線物件時）先複製一份<b>樣板連線</b>並保持關閉。
/// 之後每次查詢都從樣板再複製一份來用，好處有二：
/// 一是不會碰到 SSMS 正在使用的那條連線，使用者的長時間查詢與明確交易都不受影響；
/// 二是複製 SqlConnection 會保留認證，若改用 ConnectionString 重建，
/// SQL 驗證的密碼會因為預設不回傳而遺失。
/// </remarks>
internal sealed class SsmsConnectionSource : ISqlConnectionSource, IDisposable
{
    private readonly IDbConnection _template;
    private bool _disposed;

    private SsmsConnectionSource(IDbConnection template, string databaseName, string cacheKey)
    {
        _template = template;
        DatabaseName = databaseName;
        CacheKey = cacheKey;
    }

    public string CacheKey { get; }

    public string DatabaseName { get; }

    /// <summary>
    /// 從 SSMS 的連線建立來源；無法複製時回傳 null，呼叫端應視為「沒有資料庫建議」。
    /// </summary>
    public static SsmsConnectionSource? TryCreate(IDbConnection? editorConnection)
    {
        if (editorConnection is null)
        {
            return null;
        }

        var template = Clone(editorConnection);

        if (template is null)
        {
            SqlAssistDiagnostics.WriteAlways("無法複製目前 SQL 連線，略過資料庫物件建議");
            return null;
        }

        var databaseName = editorConnection.Database ?? string.Empty;
        var cacheKey = SqlConnectionCacheKey.Create(template.ConnectionString, databaseName);
        return new SsmsConnectionSource(template, databaseName, cacheKey);
    }

    public IDbConnection OpenConnection()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SsmsConnectionSource));
        }

        var connection = Clone(_template)
            ?? throw new InvalidOperationException("無法從樣板連線複製新的連線。");

        try
        {
            if (connection.State != ConnectionState.Open)
            {
                connection.Open();
            }

            if (!string.IsNullOrWhiteSpace(DatabaseName) &&
                !string.Equals(connection.Database, DatabaseName, StringComparison.OrdinalIgnoreCase))
            {
                // 跟隨查詢視窗選取的資料庫，而不是登入的預設資料庫。
                connection.ChangeDatabase(DatabaseName);
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _template.Dispose();
    }

    private static IDbConnection? Clone(IDbConnection source)
    {
        try
        {
            // SqlConnection 實作 ICloneable 正是為了在複製時保留認證。
            if (source is ICloneable cloneable && cloneable.Clone() is IDbConnection cloned)
            {
                return cloned;
            }
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"複製 SQL 連線失敗：{exception.Message}");
        }

        return null;
    }
}
