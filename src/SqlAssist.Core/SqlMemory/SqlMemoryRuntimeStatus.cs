using System;

namespace SqlAssist.Core.SqlMemory;

public enum SqlMemoryRuntimePhase
{
    /// <summary>設定關閉，或宿主已經卸載。</summary>
    Disabled,

    Opening,

    /// <summary>儲存已開啟，擷取、讀取與維護都在運作。</summary>
    Ready,

    /// <summary>設定是開的，但開不起儲存；下一次設定變更會再試一次。</summary>
    OpenFailed,

    /// <summary>背景寫入器遇到致命錯誤（損毀、不相容或未知）而停止；下一次設定變更會重新開啟。</summary>
    WriterFailed,
}

/// <summary>擷取沒有保存的原因；每一種都必須讓使用者看得見，不能靜靜丟掉。</summary>
public enum SqlCaptureDrop { StorageBusy, QueueFull, SnapshotTooLarge }

/// <summary>
/// 宿主狀態的不可變快照。UI 以 <see cref="Generation"/> 拒絕舊儲存的晚到回應，
/// 以 <see cref="IsAvailable"/> 決定能不能操作；文案只從這裡取，不另外拼字串。
/// </summary>
public sealed class SqlMemoryRuntimeStatus : IEquatable<SqlMemoryRuntimeStatus>
{
    public SqlMemoryRuntimeStatus(SqlMemoryRuntimePhase phase, long generation, SqlCaptureDrop? lastDrop = null,
        SqlMemoryStorageErrorKind? errorKind = null, string? errorMessage = null)
    {
        if (!Enum.IsDefined(typeof(SqlMemoryRuntimePhase), phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
        Phase = phase;
        Generation = generation;
        LastDrop = lastDrop;
        ErrorKind = errorKind;
        ErrorMessage = errorMessage;
    }

    public static SqlMemoryRuntimeStatus Initial { get; } = new(SqlMemoryRuntimePhase.Disabled, 0);

    public SqlMemoryRuntimePhase Phase { get; }

    /// <summary>每開啟或關閉一份儲存就加一；同一個世代內的回應才屬於目前畫面。</summary>
    public long Generation { get; }

    /// <summary>目前這份儲存最近一次沒有保存的擷取；換設定或重新開啟時清除。</summary>
    public SqlCaptureDrop? LastDrop { get; }

    /// <summary>開啟失敗時的分類；不是 <see cref="SqlMemoryStorageException"/> 的失敗為 null。</summary>
    public SqlMemoryStorageErrorKind? ErrorKind { get; }

    public string? ErrorMessage { get; }

    public bool IsAvailable => Phase == SqlMemoryRuntimePhase.Ready;

    /// <summary>工具窗顯示的一行狀態；一切正常時是空字串。</summary>
    public string Message => Phase switch
    {
        SqlMemoryRuntimePhase.Opening => "正在開啟 SQL Memory…",
        SqlMemoryRuntimePhase.Ready => LastDrop switch
        {
            SqlCaptureDrop.StorageBusy => "SQL Memory 資料庫忙碌中，有擷取未記錄。",
            SqlCaptureDrop.QueueFull => "SQL Memory 佇列已滿，本次未記錄。",
            SqlCaptureDrop.SnapshotTooLarge => "這份 SQL 太大，本次未記錄。",
            _ => "",
        },
        SqlMemoryRuntimePhase.OpenFailed => ErrorKind switch
        {
            SqlMemoryStorageErrorKind.Busy => "SQL Memory 資料庫正被其他程序占用，暫時無法開啟；稍後變更設定或重新啟動 SSMS 再試。",
            SqlMemoryStorageErrorKind.Incompatible => "SQL Memory 資料庫版本不相容，未開啟也不會自動重建：" + ErrorMessage,
            SqlMemoryStorageErrorKind.Corrupt => "SQL Memory 資料庫已損毀，未開啟：" + ErrorMessage,
            _ => "無法開啟 SQL Memory：" + ErrorMessage,
        },
        SqlMemoryRuntimePhase.WriterFailed => "SQL Memory 寫入失敗，已停止擷取；請檢查診斷後重新啟用。",
        _ => "SQL Memory 已停用；請在設定中啟用。",
    };

    public SqlMemoryRuntimeStatus With(SqlMemoryRuntimePhase phase, bool nextGeneration = false) =>
        new(phase, nextGeneration ? Generation + 1 : Generation);

    public SqlMemoryRuntimeStatus WithDrop(SqlCaptureDrop drop) => new(Phase, Generation, drop, ErrorKind, ErrorMessage);

    public SqlMemoryRuntimeStatus WithOpenFailure(Exception error)
    {
        if (error == null) throw new ArgumentNullException(nameof(error));
        return new(SqlMemoryRuntimePhase.OpenFailed, Generation, null,
            (error as SqlMemoryStorageException)?.Kind, error.Message);
    }

    public bool Equals(SqlMemoryRuntimeStatus? other) => other is not null && Phase == other.Phase &&
        Generation == other.Generation && LastDrop == other.LastDrop && ErrorKind == other.ErrorKind &&
        ErrorMessage == other.ErrorMessage;

    public override bool Equals(object? obj) => Equals(obj as SqlMemoryRuntimeStatus);

    public override int GetHashCode() => ((int)Phase * 397) ^ Generation.GetHashCode();
}

/// <summary>一筆擷取沒有保存；<see cref="ShouldNotify"/> 決定宿主要不要打斷使用者（狀態列）。</summary>
public sealed class SqlCaptureDroppedEventArgs : EventArgs
{
    public SqlCaptureDroppedEventArgs(SqlCaptureDrop drop, bool shouldNotify)
    {
        Drop = drop;
        ShouldNotify = shouldNotify;
    }

    public SqlCaptureDrop Drop { get; }

    /// <summary>
    /// 忙碌與佇列滿通常一連串發生，同一份儲存只提示第一次；快照太大每次都提示，那是使用者改得動的事。
    /// </summary>
    public bool ShouldNotify { get; }

    public string NotificationText => Drop switch
    {
        SqlCaptureDrop.StorageBusy => "SQL Memory 資料庫正忙，這一次沒有記錄；稍後的擷取會繼續保存。",
        SqlCaptureDrop.QueueFull => "SQL Memory 正在忙，這一次的草稿沒有記錄。",
        _ => "這份 SQL 太大，SQL Memory 這一次沒有記錄。",
    };
}
