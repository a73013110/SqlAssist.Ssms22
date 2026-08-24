using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace SqlAssist.Core;

public sealed class SettingsService
{
    private static readonly Lazy<SettingsService> DefaultInstance =
        new(() => new SettingsService(GetDefaultPath()));

    private readonly object _syncRoot = new();
    private SqlAssistSettings _settings;
    private DateTime _lastWriteTimeUtc;

    public SettingsService(string settingsPath)
    {
        SettingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));

        lock (_syncRoot)
        {
            _settings = LoadOrCreateNoLock();
        }
    }

    public static SettingsService Default => DefaultInstance.Value;

    public string SettingsPath { get; }

    public string? LastLoadError { get; private set; }

    public SqlAssistSettings GetSnapshot()
    {
        lock (_syncRoot)
        {
            ReloadIfChangedNoLock();
            return _settings.Clone();
        }
    }

    /// <summary>
    /// 以原子方式套用一組變更：先重新載入磁碟上的最新內容，套用 <paramref name="mutate"/>，
    /// 再寫回檔案。所有寫入路徑都應該走這裡，避免各自實作讀改寫而覆蓋彼此的變更。
    /// </summary>
    /// <returns>套用後的設定快照。</returns>
    public SqlAssistSettings Update(Action<SqlAssistSettings> mutate)
    {
        if (mutate is null)
        {
            throw new ArgumentNullException(nameof(mutate));
        }

        lock (_syncRoot)
        {
            ReloadIfChangedNoLock();
            mutate(_settings);
            SaveNoLock();
            return _settings.Clone();
        }
    }

    public bool ToggleEnabled()
    {
        return Update(settings => settings.Enabled = !settings.Enabled).Enabled;
    }

    public bool ToggleFeature(SqlAssistFeature feature)
    {
        return Update(settings => settings.Features.Toggle(feature)).Features.Get(feature);
    }

    public bool ToggleDiagnostics()
    {
        return Update(settings => settings.DiagnosticsEnabled = !settings.DiagnosticsEnabled)
            .DiagnosticsEnabled;
    }

    public bool ToggleAsyncCompletionProbe()
    {
        return Update(settings => settings.AsyncCompletionProbe = !settings.AsyncCompletionProbe)
            .AsyncCompletionProbe;
    }

    public bool ToggleSuggestions()
    {
        return Update(settings => settings.Suggestions.Enabled = !settings.Suggestions.Enabled)
            .Suggestions.Enabled;
    }

    public void EnsureSettingsFile()
    {
        lock (_syncRoot)
        {
            if (!File.Exists(SettingsPath))
            {
                SaveNoLock();
            }
        }
    }

    private static string GetDefaultPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SqlAssist.Ssms22",
            "settings.json");
    }

    private SqlAssistSettings LoadOrCreateNoLock()
    {
        if (!File.Exists(SettingsPath))
        {
            var defaults = new SqlAssistSettings();
            _settings = defaults;
            SaveNoLock();
            return defaults;
        }

        return LoadNoLock();
    }

    private void ReloadIfChangedNoLock()
    {
        if (!File.Exists(SettingsPath))
        {
            SaveNoLock();
            return;
        }

        var writeTimeUtc = File.GetLastWriteTimeUtc(SettingsPath);

        if (writeTimeUtc > _lastWriteTimeUtc)
        {
            _settings = LoadNoLock();
        }
    }

    private SqlAssistSettings LoadNoLock()
    {
        try
        {
            using var stream = new FileStream(
                SettingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var serializer = CreateSerializer();
            var loaded = serializer.ReadObject(stream) as SqlAssistSettings ?? new SqlAssistSettings();
            loaded.Features ??= new SqlAssistFeatureSettings();
            loaded.Suggestions ??= new SqlAssistSuggestionSettings();
            _lastWriteTimeUtc = File.GetLastWriteTimeUtc(SettingsPath);
            LastLoadError = null;
            return loaded;
        }
        catch (Exception exception)
        {
            LastLoadError = exception.Message;
            _lastWriteTimeUtc = File.GetLastWriteTimeUtc(SettingsPath);
            return _settings?.Clone() ?? new SqlAssistSettings();
        }
    }

    private void SaveNoLock()
    {
        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("設定檔路徑缺少資料夾。");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $"{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                CreateSerializer().WriteObject(stream, _settings);
                stream.Flush(true); // 先確保暫存檔完整寫入磁碟，再取代正式設定檔。
            }

            if (File.Exists(SettingsPath))
            {
                File.Replace(tempPath, SettingsPath, null, true); // 同一磁碟區內原子取代。
            }
            else
            {
                File.Move(tempPath, SettingsPath);
            }

            _lastWriteTimeUtc = File.GetLastWriteTimeUtc(SettingsPath);
            LastLoadError = null;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static DataContractJsonSerializer CreateSerializer()
    {
        return new DataContractJsonSerializer(typeof(SqlAssistSettings));
    }
}
