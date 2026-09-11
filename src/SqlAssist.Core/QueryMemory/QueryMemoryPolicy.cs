using System;

namespace SqlAssist.Core.QueryMemory;

/// <summary>由宿主設定轉成不可變政策；核心不依賴 SSMS 的設定服務。</summary>
public sealed class QueryMemoryPolicy
{
    public QueryMemoryPolicy(bool recoveryEnabled, bool autoRevisionEnabled, TimeSpan autoRevisionInterval,
        bool captureExecutedSql, bool captureUnexecutedDrafts)
    {
        if (autoRevisionInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(autoRevisionInterval));
        RecoveryEnabled = recoveryEnabled;
        AutoRevisionEnabled = autoRevisionEnabled;
        AutoRevisionInterval = autoRevisionInterval;
        CaptureExecutedSql = captureExecutedSql;
        CaptureUnexecutedDrafts = captureUnexecutedDrafts;
    }

    public bool RecoveryEnabled { get; }
    public bool AutoRevisionEnabled { get; }
    public TimeSpan AutoRevisionInterval { get; }
    public bool CaptureExecutedSql { get; }
    public bool CaptureUnexecutedDrafts { get; }
}
