using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22;

internal static class SqlAssistDiagnostics
{
    private static readonly object SyncRoot = new();

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
            var viewDetails = textView is null ? string.Empty : DescribeView(textView);
            var line = $"{DateTimeOffset.Now:O} | {message}{viewDetails}{Environment.NewLine}";

            lock (SyncRoot)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // 記錄功能不可影響 SSMS 編輯器；任何檔案系統錯誤都直接忽略。
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
