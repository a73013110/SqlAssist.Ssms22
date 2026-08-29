using System;
using System.IO;
using System.Text;
using System.Threading;
using SqlAssist.Core.Json;
using SqlAssist.Core.Snippets;
using SqlAssist.Ssms22;

namespace SqlAssist.Ssms22.Snippets;

/// <summary>
/// Snippet 檔的讀寫與行程內快取。
/// </summary>
/// <remarks>
/// 刻意不放進 Unified Settings：那裡只收 boolean、integer、enum 與 string，
/// 一份可增刪的清單塞不進去。改成獨立的 JSON 檔之後，使用者也可以直接用
/// 編輯器改，或整份複製到另一台機器。
///
/// 讀取失敗一律降級而不是丟例外——這個型別掛在建議清單的路徑上，
/// 一個手改壞掉的檔案不該讓整個建議功能消失。<see cref="LastError"/> 留給
/// 管理介面顯示原因。
/// </remarks>
internal static class SqlSnippetStore
{
    private static readonly object Gate = new();
    private static SqlSnippetLibrary? _current;

    /// <summary>Snippet 檔的完整路徑。</summary>
    /// <remarks>
    /// 放在 <c>%APPDATA%</c> 而不是 <c>Documents</c>：這是設定而不是文件，
    /// 而且不該被 OneDrive 之類的資料夾同步在多台機器之間打架。
    /// </remarks>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SqlAssist",
        "snippets.json");

    /// <summary>上一次讀取或寫入失敗的原因；成功時為 null。</summary>
    public static string? LastError { get; private set; }

    /// <summary>目前的清單；第一次取用時才讀檔。</summary>
    public static SqlSnippetLibrary Current
    {
        get
        {
            var cached = Volatile.Read(ref _current);

            if (cached is not null)
            {
                return cached;
            }

            lock (Gate)
            {
                _current ??= Load();
                return _current;
            }
        }
    }

    /// <summary>丟掉快取，下一次取用時重新讀檔。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _current = null;
        }
    }

    /// <summary>
    /// 寫回檔案並更新快取。
    /// </summary>
    /// <returns>成功時為 true；失敗時為 false，原因見 <see cref="LastError"/>。</returns>
    public static bool Save(SqlSnippetLibrary library)
    {
        if (library is null)
        {
            throw new ArgumentNullException(nameof(library));
        }

        lock (Gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);

                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 先寫暫存檔再置換：存檔途中當掉時，使用者失去的是這一次的修改，
                // 而不是整份 Snippet。
                var temporaryPath = FilePath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    SqlSnippetSerializer.Serialize(library),
                    new UTF8Encoding(false));

                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }

                File.Move(temporaryPath, FilePath);

                _current = library;
                LastError = null;
                SqlAssistDiagnostics.Write($"已寫入 {library.Count} 筆 Snippet：{FilePath}");
                return true;
            }
            // 不走 SqlAssistPlatformGuard：這裡只接檔案系統的預期失敗，其餘
            // （序列化的程式錯誤）該讓它浮出來；而且失敗的原因要留給管理員視窗顯示。
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LastError = exception.Message;
                SqlAssistDiagnostics.WriteAlways($"寫入 Snippet 失敗：{exception}");
                return false;
            }
        }
    }

    private static SqlSnippetLibrary Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                // 第一次啟動：把預設清單寫成檔案，使用者才看得到可以改什麼。
                var defaults = SqlSnippetLibrary.CreateDefault();
                Save(defaults);
                return defaults;
            }

            var library = SqlSnippetSerializer.Deserialize(File.ReadAllText(FilePath));
            LastError = null;
            return library;
        }
        catch (JsonParseException exception)
        {
            // 檔案壞掉時退回空清單，不要用預設清單蓋掉——使用者的內容還在檔案裡，
            // 之後修好格式就會回來。用預設清單覆蓋等於幫他刪光。
            LastError = $"{FilePath} 的格式不正確：{exception.Message}";
            SqlAssistDiagnostics.WriteAlways($"讀取 Snippet 失敗：{exception}");
            return SqlSnippetLibrary.Empty;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastError = exception.Message;
            SqlAssistDiagnostics.WriteAlways($"讀取 Snippet 失敗：{exception}");
            return SqlSnippetLibrary.Empty;
        }
    }
}
