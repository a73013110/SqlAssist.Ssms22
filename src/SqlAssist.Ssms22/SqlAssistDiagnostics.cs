using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22;

/// <summary>
/// 診斷紀錄；寫入排隊，實際開檔在背景批次完成。
/// </summary>
/// <remarks>
/// 每一筆都同步 <c>CreateDirectory</c>＋<c>AppendAllText</c> 的版本，是在完成工作的那條
/// 執行緒上、在全域鎖裡開檔寫檔關檔。通知接上高頻工作又打開詳細模式之後，那會變成每秒
/// 數十次開關檔，而且擋的是剛做完事的那條執行緒。改成有界佇列加背景批次：呼叫端只付
/// 一次字串配置與入列，檔案 I/O 一次寫一批。
///
/// 佇列滿了就丟，並記住丟掉幾筆——診斷不能反過來吃掉記憶體，而「這裡少了 N 筆」比
/// 悄悄少幾行更有用。
/// </remarks>
internal static class SqlAssistDiagnostics
{
    private static readonly object SyncRoot = new();

    /// <summary>批次視窗；再高頻的紀錄也收斂成每 250 ms 一次開檔。</summary>
    private const int FlushDelayMilliseconds = 250;

    /// <summary>待寫上限；超過就丟掉並計數，不讓紀錄本身變成記憶體壓力。</summary>
    private const int QueueLimit = 2000;

    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly Timer FlushTimer = new(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    private static int _pendingCount;
    private static int _dropped;
    private static int _scheduled;

    internal static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SqlAssist.Ssms22",
        "SqlAssist.log");

    public static void Write(string message, ITextView? textView = null)
    {
        if (!SqlAssistSettingsStore.Current.VerboseLogging)
        {
            return;
        }

        WriteAlways(message, textView);
    }

    /// <summary>確保紀錄檔存在，讓「開啟診斷紀錄檔」不會開到一個不存在的路徑。</summary>
    public static void EnsureLogFile()
    {
        // 讀之前先把待寫的倒出去，否則剛發生的事還在佇列裡，開起來的檔案是舊的。
        Flush();

        lock (SyncRoot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));

            if (!File.Exists(LogPath))
            {
                File.WriteAllText(LogPath, string.Empty);
            }
        }
    }

    public static void WriteAlways(string message, ITextView? textView = null)
    {
        try
        {
            // 檢視描述在呼叫端這一側取；排到背景才讀就可能碰到已經關掉的編輯器。
            var viewDetails = textView is null ? string.Empty : DescribeView(textView);
            Enqueue($"{DateTimeOffset.Now:O} | {message}{viewDetails}{Environment.NewLine}");
        }
        catch
        {
            // 記錄功能不可影響 SSMS 編輯器；描述檢視或入列失敗都直接忽略。
            // 不走 SqlAssistPlatformGuard：那一族失敗時要寫紀錄，而這裡正是紀錄本身。
        }
    }

    /// <summary>把待寫的紀錄全部寫出；套件卸載時必須呼叫，否則最後一批會留在記憶體裡。</summary>
    public static void Flush()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        var batch = new StringBuilder();
        while (Pending.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _pendingCount);
            batch.Append(line);
        }

        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0) batch.Append($"{DateTimeOffset.Now:O} | 診斷佇列已滿，略過 {dropped} 筆紀錄{Environment.NewLine}");
        if (batch.Length == 0) return;
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, batch.ToString());
            }
        }
        catch
        {
            // 記錄功能不可影響 SSMS 編輯器；任何檔案系統錯誤都直接忽略。
            // 不走 SqlAssistPlatformGuard：那一族失敗時要寫紀錄，而這裡正是紀錄本身。
        }

        // 倒空期間又進來的那幾筆要有人負責寫出去。
        if (!Pending.IsEmpty) Schedule();
    }

    private static void Enqueue(string line)
    {
        if (Interlocked.Increment(ref _pendingCount) > QueueLimit)
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _dropped);
            return;
        }

        Pending.Enqueue(line);
        Schedule();
    }

    private static void Schedule()
    {
        // 只在還沒排程時開始計時；每一筆都往後推的話，持續寫入就永遠等不到那一次寫檔。
        if (Interlocked.Exchange(ref _scheduled, 1) != 0) return;
        try
        {
            FlushTimer.Change(FlushDelayMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 行程收尾時計時器可能已經回收；剩下的交給 Flush 的同步路徑。
            Interlocked.Exchange(ref _scheduled, 0);
        }
    }

    private static string DescribeView(ITextView textView)
    {
        var contentType = textView.TextBuffer.ContentType;
        var baseTypes = string.Join(",", contentType.BaseTypes.Select(type => type.TypeName));
        var roles = string.Join(",", textView.Roles);
        return $" | ContentType={contentType.TypeName} | BaseTypes={baseTypes} | Roles={roles}";
    }
}
