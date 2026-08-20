using System;
using System.Threading;

namespace SqlAssist.Ssms22;

internal static class SqlAssistRuntimeState
{
    private static readonly object SyncRoot = new();
    private static int _textViewCount;
    private static int _tabCount;
    private static string _lastTabSource = "尚未收到 Tab";
    private static string _lastExpansion = "尚未展開";

    public static bool PackageLoaded { get; private set; }

    public static int TextViewCount => Volatile.Read(ref _textViewCount);

    public static int TabCount => Volatile.Read(ref _tabCount);

    public static string LastTabSource
    {
        get
        {
            lock (SyncRoot)
            {
                return _lastTabSource;
            }
        }
    }

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

    public static void MarkTabReceived(string source)
    {
        Interlocked.Increment(ref _tabCount);

        lock (SyncRoot)
        {
            _lastTabSource = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}，來源：{source}";
        }
    }

    public static void MarkExpansion(string replacement)
    {
        lock (SyncRoot)
        {
            _lastExpansion = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}，內容：{replacement}";
        }
    }
}

