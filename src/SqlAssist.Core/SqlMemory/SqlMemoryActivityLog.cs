using System;
using System.Collections.Generic;

namespace SqlAssist.Core.SqlMemory;

public enum SqlMemoryActivityKind
{
    /// <summary>背景排程的維護；連續幾批合併成一筆，不讓清單被每 30 秒一批灌滿。</summary>
    ScheduledMaintenance,
    ManualMaintenance,
    Cleanup,
    Compact,
    Backup,
}

/// <param name="DeletedRows">刪除的資料列數；壓縮與備份為 0。</param>
/// <param name="Bytes">清理釋出的內容位元組、壓縮後縮小的檔案位元組，或備份檔大小。</param>
/// <param name="Failure">失敗時的一行原因；成功為 null。</param>
public sealed record SqlMemoryActivity(DateTimeOffset At, SqlMemoryActivityKind Kind, long DeletedRows, long Bytes,
    string? Failure = null)
{
    public bool Succeeded => Failure == null;
}

/// <summary>
/// 本次 SSMS 工作階段的清理紀錄；只在記憶體，最新在前、有上限。
/// </summary>
/// <remarks>
/// 不寫進資料庫：它是「剛才發生了什麼」的回饋，不是稽核紀錄；為它加表會讓 schema 與維護多一條保留規則。
/// </remarks>
public sealed class SqlMemoryActivityLog
{
    public const int Capacity = 20;

    /// <summary>背景維護超過這段時間沒有再刪除，就另起一筆，不和上一輪合併。</summary>
    public static readonly TimeSpan ScheduledMergeWindow = TimeSpan.FromMinutes(30);

    private readonly object _sync = new();
    private readonly List<SqlMemoryActivity> _items = new();

    public void Record(SqlMemoryActivity activity)
    {
        if (activity == null) throw new ArgumentNullException(nameof(activity));
        lock (_sync)
        {
            if (activity.Kind == SqlMemoryActivityKind.ScheduledMaintenance && activity.Succeeded && _items.Count > 0 &&
                _items[0] is { Kind: SqlMemoryActivityKind.ScheduledMaintenance, Succeeded: true } last &&
                activity.At - last.At < ScheduledMergeWindow)
            {
                _items[0] = last with { At = activity.At, DeletedRows = last.DeletedRows + activity.DeletedRows,
                    Bytes = last.Bytes + activity.Bytes };
                return;
            }
            _items.Insert(0, activity);
            if (_items.Count > Capacity) _items.RemoveAt(_items.Count - 1);
        }
    }

    public IReadOnlyList<SqlMemoryActivity> Snapshot()
    {
        lock (_sync) return _items.ToArray();
    }
}
