using System;
using System.Threading;

namespace SqlAssist.Ssms22;

/// <summary>診斷對話框要回答「它到底有沒有在跑」時需要的幾個計數。</summary>
internal static class SqlAssistRuntimeState
{
    private static readonly object SyncRoot = new();
    private static int _textViewCount;
    private static string _lastExpansion = "尚未展開";

    public static bool PackageLoaded { get; private set; }

    public static int TextViewCount => Volatile.Read(ref _textViewCount);

    public static string LastExpansion
    {
        get
        {
            lock (SyncRoot)
            {
                return _lastExpansion;
            }
        }
    }

    public static void MarkPackageLoaded()
    {
        PackageLoaded = true;
    }

    public static void MarkTextViewCreated()
    {
        Interlocked.Increment(ref _textViewCount);
    }

    public static void MarkExpansion(string replacement)
    {
        lock (SyncRoot)
        {
            _lastExpansion = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}，內容：{replacement}";
        }
    }
}
