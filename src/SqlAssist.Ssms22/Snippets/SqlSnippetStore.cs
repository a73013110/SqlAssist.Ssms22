using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using SqlAssist.Core.Json;
using SqlAssist.Core.Snippets;
using SqlAssist.Ssms22;

namespace SqlAssist.Ssms22.Snippets;

/// <summary>Snippet override 檔的讀寫與行程內快取。</summary>
/// <remarks>
/// 內建值在 Core 的內嵌 JSON；使用者檔只存差異。檔案不存在是正常狀態，
/// 第一次啟動不能把 43 筆預設值物化，否則下一版 VSIX 再也更新不到它們。
/// </remarks>
internal static class SqlSnippetStore
{
    private static readonly object Gate = new();
    private static SqlSnippetConfiguration? _configuration;
    private static volatile bool _readOnly;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SqlAssist",
        "snippets.json");

    public static string LegacyBackupPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SqlAssist",
        "snippets.v1.backup.json");

    public static string? LastError { get; private set; }

    public static bool IsReadOnly
    {
        get
        {
            EnsureLoaded();
            return _readOnly;
        }
    }

    /// <summary>建議清單使用的穩定參考；只有成功存檔後才會換成新實例。</summary>
    public static SqlSnippetLibrary Current => Configuration.Library;

    /// <summary>管理介面使用的完整狀態，包含停用的內建項目。</summary>
    public static SqlSnippetConfiguration Configuration
    {
        get
        {
            EnsureLoaded();
            return Volatile.Read(ref _configuration)!;
        }
    }

    /// <summary>
    /// 把管理介面的完整清單寫回檔案。
    /// </summary>
    /// <remarks>
    /// 收的是全部項目而不是有效清單：被同捷徑遮住的項目仍然是使用者的資料，
    /// 只傳有效清單會讓它被當成「已刪除」寫成停用紀錄。
    /// </remarks>
    public static bool Save(IReadOnlyList<SqlSnippetConfigurationEntry> entries)
    {
        if (entries is null)
        {
            throw new ArgumentNullException(nameof(entries));
        }

        lock (Gate)
        {
            EnsureLoadedInsideLock();

            if (_readOnly)
            {
                LastError ??= "Snippet 檔案來自較新的 SqlAssist 版本，目前只能讀取，不能覆寫。";
                return false;
            }

            try
            {
                var document = SqlSnippetMerger.CreateOverrides(entries, SqlSnippetDefaults.Current);
                WriteDocument(document);
                _configuration = SqlSnippetMerger.Merge(SqlSnippetDefaults.Current, document);
                LastError = null;
                SqlAssistDiagnostics.Write(
                    $"已寫入 {document.Snippets.Count} 筆 Snippet 差異；有效項目 {_configuration.Library.Count} 筆：{FilePath}");
                return true;
            }
            // 不走 SqlAssistPlatformGuard：這裡只接檔案系統的預期失敗，其餘
            // 商業邏輯錯誤必須浮出；而失敗原因要留給管理員視窗顯示。
            catch (Exception exception) when (IsFileFailure(exception))
            {
                LastError = exception.Message;
                SqlAssistDiagnostics.WriteAlways($"寫入 Snippet 失敗：{exception}");
                return false;
            }
        }
    }

    private static void EnsureLoaded()
    {
        if (Volatile.Read(ref _configuration) is not null)
        {
            return;
        }

        lock (Gate)
        {
            EnsureLoadedInsideLock();
        }
    }

    private static void EnsureLoadedInsideLock()
    {
        if (_configuration is null)
        {
            _configuration = Load();
        }
    }

    private static SqlSnippetConfiguration Load()
    {
        var defaults = SqlSnippetDefaults.Current;

        if (SqlSnippetDefaults.LastError is { } resourceError)
        {
            // 建置期的錯，執行期救不了；但擴充仍要能用，所以只記錄不停擺。
            SqlAssistDiagnostics.WriteAlways($"內建 Snippet 無法載入：{resourceError}");
        }

        try
        {
            if (!File.Exists(FilePath))
            {
                LastError = null;
                _readOnly = false;
                return SqlSnippetMerger.Merge(defaults, SqlSnippetDocument.Empty);
            }

            var original = File.ReadAllText(FilePath);
            var document = SqlSnippetSerializer.DeserializeDocument(original);

            if (document.IsNewerThanSupported)
            {
                _readOnly = true;
                LastError =
                    $"{FilePath} 的格式版本是 {document.Version}，目前只支援到 " +
                    $"{SqlSnippetLibrary.CurrentVersion}；已進入唯讀模式。";
                return SqlSnippetMerger.Merge(defaults, document);
            }

            if (document.Version == 1)
            {
                WriteLegacyBackupOnce();
                document = SqlSnippetMerger.MigrateVersion1(document, defaults);
                WriteDocument(document);
                SqlAssistDiagnostics.WriteAlways(
                    $"已把 Snippet v1 遷移成 v2；原檔保留於 {LegacyBackupPath}");
            }

            _readOnly = false;
            LastError = null;
            return SqlSnippetMerger.Merge(defaults, document);
        }
        catch (JsonParseException exception)
        {
            // 壞掉的 override 不能讓內建片段一起消失，也絕不能覆蓋原檔。
            _readOnly = true;
            LastError = $"{FilePath} 的格式不正確：{exception.Message}";
            SqlAssistDiagnostics.WriteAlways($"讀取 Snippet 失敗：{exception}");
            return SqlSnippetMerger.Merge(defaults, SqlSnippetDocument.Empty);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            _readOnly = true;
            LastError = exception.Message;
            SqlAssistDiagnostics.WriteAlways($"讀取 Snippet 失敗：{exception}");
            return SqlSnippetMerger.Merge(defaults, SqlSnippetDocument.Empty);
        }
    }

    private static void WriteLegacyBackupOnce()
    {
        if (File.Exists(LegacyBackupPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(LegacyBackupPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // File.Copy 保留原始位元組與編碼；overwrite=false 也讓兩個 SSMS 行程
        // 同時遷移時不會覆蓋先完成的那一份。
        try
        {
            File.Copy(FilePath, LegacyBackupPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(LegacyBackupPath))
        {
            // 另一個 SSMS 行程先完成了同一份冪等遷移；既有備份才是應保留的那份。
        }
    }

    private static void WriteDocument(SqlSnippetDocument document)
    {
        var directory = Path.GetDirectoryName(FilePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // SSMS 可以同時開多個行程；每次使用自己的暫存檔，避免互相覆寫尚未完成的內容。
        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllText(
                temporaryPath,
                SqlSnippetSerializer.Serialize(document),
                new UTF8Encoding(false));

            if (File.Exists(FilePath))
            {
                // Delete + Move 中間當掉會整份消失；Replace 在同一個磁碟區是原子置換。
                File.Replace(temporaryPath, FilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, FilePath);
            }
        }
        finally
        {
            // 清暫存檔本身失敗不能蓋掉真正的原因：從 finally 丟出去的例外會取代
            // 正在往上傳的那一個，於是使用者看到的是「刪不掉暫存檔」而不是
            // 「磁碟已滿」。留一個孤兒暫存檔，下一次存檔會用新的名字。
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                SqlAssistDiagnostics.Write($"清除 Snippet 暫存檔失敗：{exception.Message}");
            }
        }
    }

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException;
}
