using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>寫回說明的結果。</summary>
public enum SqlDescriptionWriteStatus
{
    /// <summary>全部寫進去了。</summary>
    Applied,

    /// <summary>有目標在載入之後被別人改過；一項都沒寫。</summary>
    Conflicted,

    /// <summary>伺服器拒絕（權限、連不上、逾時）；一項都沒寫。</summary>
    Failed
}

/// <summary>被別人先改過的一項：載入時的值、伺服器上現在的值。</summary>
public sealed class SqlDescriptionConflict
{
    public SqlDescriptionConflict(SqlDescriptionChange change, string current)
    {
        Change = change ?? throw new ArgumentNullException(nameof(change));
        Current = SqlDescriptionValue.Normalize(current);
    }

    public SqlDescriptionChange Change { get; }

    public string Current { get; }
}

public sealed class SqlDescriptionWriteResult
{
    private SqlDescriptionWriteResult(
        SqlDescriptionWriteStatus status,
        IReadOnlyList<SqlDescriptionConflict> conflicts,
        string? message)
    {
        Status = status;
        Conflicts = conflicts;
        Message = message;
    }

    public SqlDescriptionWriteStatus Status { get; }

    public IReadOnlyList<SqlDescriptionConflict> Conflicts { get; }

    /// <summary>失敗時伺服器說的那一句，原樣給人看（不翻譯）。</summary>
    public string? Message { get; }

    public static SqlDescriptionWriteResult Applied { get; } =
        new(SqlDescriptionWriteStatus.Applied, Array.Empty<SqlDescriptionConflict>(), null);

    public static SqlDescriptionWriteResult Conflicted(IReadOnlyList<SqlDescriptionConflict> conflicts) =>
        new(SqlDescriptionWriteStatus.Conflicted, conflicts, null);

    public static SqlDescriptionWriteResult Failed(string message) =>
        new(SqlDescriptionWriteStatus.Failed, Array.Empty<SqlDescriptionConflict>(), message);
}

/// <summary>
/// 在一個交易裡寫回一批說明：先確認沒有人先改過，再全部寫入，任何一項失敗整批退回。
/// </summary>
/// <remarks>
/// 只有說明走這一條「預覽後直接執行」：擴充屬性不動資料、不鎖資料表，退回也沒有副作用。
/// 會動到結構的變更不在這裡寫——那要使用者在查詢視窗裡自己按執行。
///
/// 併發檢查與寫入在同一個交易、同一條連線上：檢查完到寫入之間仍有一個很短的空檔，
/// <c>fn_listextendedproperty</c> 加不了鎖定提示；那個空檔裡別人改的會被這一批覆蓋，
/// 與兩個人在 SSMS 屬性對話框裡各按一次確定是同一個結果。
/// </remarks>
public static class SqlDescriptionWriter
{
    /// <summary>寫入逾時。</summary>
    public const int DefaultCommandTimeoutSeconds = 60;

    public static SqlDescriptionWriteResult Write(
        ISqlConnectionSource connectionSource,
        IReadOnlyList<SqlDescriptionChange> changes,
        CancellationToken cancellationToken,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        if (connectionSource is null) throw new ArgumentNullException(nameof(connectionSource));
        if (changes is null) throw new ArgumentNullException(nameof(changes));
        if (changes.Count == 0) return SqlDescriptionWriteResult.Applied;

        try
        {
            using var connection = connectionSource.OpenConnection();
            using var transaction = connection.BeginTransaction();

            var conflicts = FindConflicts(connection, transaction, changes, commandTimeoutSeconds, cancellationToken);

            if (conflicts.Count > 0)
            {
                transaction.Rollback();
                return SqlDescriptionWriteResult.Conflicted(conflicts);
            }

            cancellationToken.ThrowIfCancellationRequested();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = SqlDescriptionScript.Statements(changes, "\r\n");
                command.CommandTimeout = commandTimeoutSeconds;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            return SqlDescriptionWriteResult.Applied;
        }
        catch (DbException exception)
        {
            // 交易隨連線一起釋放時就退回了；這裡不再 Rollback，連線斷掉時那一句也會丟例外。
            SqlMetadataFailure.Report("寫回資料字典說明：" + connectionSource.DatabaseName, exception);
            return SqlDescriptionWriteResult.Failed(exception.Message);
        }
    }

    private static List<SqlDescriptionConflict> FindConflicts(
        IDbConnection connection,
        IDbTransaction transaction,
        IReadOnlyList<SqlDescriptionChange> changes,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var current = new string[changes.Count];

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SqlDescriptionScript.CurrentValuesQuery(changes, "\r\n");
            command.CommandTimeout = commandTimeoutSeconds;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = reader.GetInt32(0);
                if (index >= 0 && index < current.Length) current[index] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }
        }

        var conflicts = new List<SqlDescriptionConflict>();

        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            var value = current[index] ?? string.Empty;

            // 別人改成的正好是這裡要寫的值也不算衝突：結果一樣。
            if (!SqlDescriptionValue.AreEqual(value, change.Original) && !SqlDescriptionValue.AreEqual(value, change.Value))
            {
                conflicts.Add(new SqlDescriptionConflict(change, value));
            }
        }

        return conflicts;
    }
}
