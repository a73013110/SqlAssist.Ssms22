using System;
using System.Threading;
using SqlAssist.Core.Diagnostics;

namespace SqlAssist.Ssms22;

/// <summary>「關於與診斷」要回答擴充是否真的在運作時所需的工作階段狀態。</summary>
internal static class SqlAssistRuntimeState
{
    private static readonly object SyncRoot = new();
    private static int _textViewCount;
    private static SqlAssistActivity _lastActivity;

    public static bool PackageReady { get; private set; }

    public static int OpenTextViewCount => Volatile.Read(ref _textViewCount);

    public static SqlAssistActivity LastActivity
    {
        get
        {
            lock (SyncRoot)
            {
                return _lastActivity;
            }
        }
    }

    public static void MarkPackageReady()
    {
        PackageReady = true;
    }

    public static void MarkTextViewCreated()
    {
        Interlocked.Increment(ref _textViewCount);
    }

    /// <remarks>
    /// 刻意夾在零：關閉事件是由編輯器發出的，我們不保證每一次都對得上一次建立
    /// （例如接聽器初始化失敗那一次）。診斷顯示負數只會讓人去追一個不存在的問題。
    /// </remarks>
    public static void MarkTextViewClosed()
    {
        while (true)
        {
            var current = Volatile.Read(ref _textViewCount);

            if (current <= 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _textViewCount, current - 1, current) == current)
            {
                return;
            }
        }
    }

    public static void MarkActivity(SqlAssistActivityKind kind, int affectedItemCount = 0)
    {
        lock (SyncRoot)
        {
            _lastActivity = new SqlAssistActivity(kind, DateTimeOffset.Now, affectedItemCount);
        }
    }
}
