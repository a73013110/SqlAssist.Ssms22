using System;
using Microsoft.Internal.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;
using SqlAssist.Core;

namespace SqlAssist.Ssms22.Settings;

/// <summary>
/// 從 SSMS 的 Unified Settings 讀出一份 <see cref="SqlAssistSettings"/> 快照並保持最新。
/// </summary>
/// <remarks>
/// 為什麼要快取而不是每次都問 reader：建議來源的
/// <c>InitializeCompletion</c> 與輸入字元的處理常式都在按鍵路徑上，
/// 每按一次鍵去查十四個 moniker 是不必要的成本。改成啟動時讀一次、
/// 之後由 <see cref="ISettingsReader.SubscribeToChanges"/> 推更新。
///
/// 這個類別<b>永遠有值</b>：服務缺席、manifest 還沒註冊、值型別不符，
/// 一律回退到 <see cref="SqlAssistSettings"/> 的屬性預設值。
/// 設定讀不到是一回事，擴充因此不能用是另一回事。
/// </remarks>
internal static class SqlAssistSettingsStore
{
    /// <summary>寫入時標示變更來源；出現在 Unified Settings 的變更通知裡。</summary>
    private const string WriterId = "SqlAssist";

    private static readonly object SyncRoot = new();

    private static volatile SqlAssistSettings _current = new();
    private static ISettingsManager? _manager;
    private static ISettingsReader? _reader;
    private static IDisposable? _subscription;

    /// <summary>目前生效的設定。任何時候都可以讀，不會回傳 null。</summary>
    public static SqlAssistSettings Current => _current;

    /// <summary>
    /// 接上 Unified Settings。重複呼叫只有第一次會生效。
    /// </summary>
    /// <remarks>
    /// 必須在 UI 執行緒上呼叫（取得全域服務的要求）。套件載入與第一個 SQL
    /// 編輯器建立都會呼叫——兩者的先後順序不保證，所以兩邊都要叫。
    /// </remarks>
    public static void Initialize(IServiceProvider serviceProvider)
    {
        if (_reader is not null || serviceProvider is null)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_reader is not null)
            {
                return;
            }

            try
            {
                _manager = serviceProvider.GetService(typeof(SVsUnifiedSettingsManager)) as ISettingsManager;

                if (_manager is null)
                {
                    SqlAssistDiagnostics.WriteAlways(
                        "取不到 SVsUnifiedSettingsManager，SqlAssist 改用內建預設值執行");
                    return;
                }

                var reader = _manager.GetReader();
                _current = Read(reader);
                _reader = reader;

                // 訂閱回呼可能來自任何執行緒；這裡只換掉一個 volatile 欄位，
                // 讀取端拿到的永遠是完整的一份快照。
                _subscription = reader.SubscribeToChanges(_ => Reload(), SqlAssistMonikers.All);
                SqlAssistDiagnostics.WriteAlways("已接上 Unified Settings");
            }
            catch (Exception exception)
            {
                // 設定讀不到不可以讓編輯器開不起來。
                SqlAssistDiagnostics.WriteAlways($"初始化 Unified Settings 失敗：{exception.Message}");
            }
        }
    }

    /// <summary>套件卸載時解除訂閱；訂閱物件活到這裡為止。</summary>
    public static void Shutdown()
    {
        lock (SyncRoot)
        {
            _subscription?.Dispose();
            _subscription = null;
            _reader = null;
            _manager = null;
        }
    }

    /// <summary>切換一個布林設定並回傳切換後的值。</summary>
    public static bool Toggle(string moniker, bool currentValue)
    {
        var value = !currentValue;
        return TrySetValue(moniker, value) ? value : currentValue;
    }

    /// <summary>
    /// 寫入一個設定值。
    /// </summary>
    /// <returns>成功送出並提交時為 <c>true</c>。</returns>
    public static bool TrySetValue<T>(string moniker, T value)
        where T : notnull
    {
        try
        {
            if (_manager is null)
            {
                SqlAssistDiagnostics.WriteAlways($"設定 {moniker} 無法寫入：Unified Settings 尚未接上");
                return false;
            }

            var writer = _manager.GetWriter(WriterId);
            var change = writer.EnqueueChange(moniker, value);

            if (change.Outcome is not (SettingChangeOutcome.PendingCommit
                or SettingChangeOutcome.PendingCommitWithoutValidation
                or SettingChangeOutcome.NoOp))
            {
                SqlAssistDiagnostics.WriteAlways(
                    $"設定 {moniker} 未被接受：{change.Outcome} {change.Message}");
                return false;
            }

            var commit = writer.RequestCommit(WriterId);

            if (commit.Outcome is not (SettingCommitOutcome.Success
                or SettingCommitOutcome.NoChangesQueued
                or SettingCommitOutcome.PendingApproval))
            {
                SqlAssistDiagnostics.WriteAlways(
                    $"設定 {moniker} 提交失敗：{commit.Outcome} {commit.Message}");
                return false;
            }

            // 訂閱回呼通常會跟著來，但別依賴它的時機：命令處理常式在下一行
            // 就要用勾選狀態回答選單，快取必須當場就是新的。
            Reload();
            return true;
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"寫入設定 {moniker} 失敗：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 讀取 SSMS 內建 T-SQL IntelliSense 的開關。
    /// </summary>
    /// <returns>讀不到時為 <c>null</c>；那代表 SSMS 沒有註冊這個設定，不代表它是關的。</returns>
    public static bool? TryGetNativeIntelliSenseEnabled()
    {
        try
        {
            if (_reader is null)
            {
                return null;
            }

            var result = _reader.GetValue<bool>(
                SqlAssistMonikers.NativeIntelliSenseEnabled,
                SettingReadOptions.NoRequirements);

            return result.Outcome == SettingRetrievalOutcome.Success ? result.Value : null;
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"讀取 SSMS 內建 IntelliSense 狀態失敗：{exception.Message}");
            return null;
        }
    }

    private static void Reload()
    {
        try
        {
            if (_reader is { } reader)
            {
                _current = Read(reader);
            }
        }
        catch (Exception exception)
        {
            // 保留上一份可用的快照，總比切回預設值讓使用者的設定突然失效好。
            SqlAssistDiagnostics.WriteAlways($"重新讀取設定失敗：{exception.Message}");
        }
    }

    private static SqlAssistSettings Read(ISettingsReader reader)
    {
        var defaults = new SqlAssistSettings();

        return new SqlAssistSettings
        {
            Enabled = Read(reader, SqlAssistMonikers.Enabled, defaults.Enabled),
            UppercaseKeywordsOnType = Read(
                reader,
                SqlAssistMonikers.UppercaseKeywordsOnType,
                defaults.UppercaseKeywordsOnType),

            SuggestionsEnabled = Read(
                reader,
                SqlAssistMonikers.SuggestionsEnabled,
                defaults.SuggestionsEnabled),
            TriggerAfterCharacters = SqlAssistLimits.ClampTriggerCharacters(
                Read(reader, SqlAssistMonikers.TriggerAfterCharacters, defaults.TriggerAfterCharacters)),
            IncludeSnippets = Read(reader, SqlAssistMonikers.IncludeSnippets, defaults.IncludeSnippets),
            IncludeDatabaseObjects = Read(
                reader,
                SqlAssistMonikers.IncludeDatabaseObjects,
                defaults.IncludeDatabaseObjects),
            QualifyObjectNames = Read(
                reader,
                SqlAssistMonikers.QualifyObjectNames,
                defaults.QualifyObjectNames),
            UseSquareBrackets = Read(
                reader,
                SqlAssistMonikers.UseSquareBrackets,
                defaults.UseSquareBrackets),

            HoverEnabled = Read(reader, SqlAssistMonikers.HoverEnabled, defaults.HoverEnabled),
            PreviewMode = ParsePreviewMode(
                Read(reader, SqlAssistMonikers.PreviewMode, string.Empty),
                defaults.PreviewMode),
            PreviewDelayMilliseconds = SqlAssistLimits.ClampPreviewDelay(
                Read(reader, SqlAssistMonikers.PreviewDelay, defaults.PreviewDelayMilliseconds)),
            PreviewPlacement = ParsePlacement(
                Read(reader, SqlAssistMonikers.PreviewPlacement, string.Empty),
                defaults.PreviewPlacement),
            PreviewFontSize = SqlAssistLimits.ClampPreviewFontSize(
                Read(reader, SqlAssistMonikers.PreviewFontSize, (int)defaults.PreviewFontSize)),

            VerboseLogging = Read(reader, SqlAssistMonikers.VerboseLogging, defaults.VerboseLogging)
        };
    }

    /// <remarks>
    /// 用 <see cref="SettingReadOptions.NoRequirements"/> 而不是 <c>GetValueOrThrow</c>：
    /// 這條路徑在編輯器啟動與設定變更時跑，任何一個 moniker 出問題都不該
    /// 讓其餘十三個跟著失效。
    /// </remarks>
    private static T Read<T>(ISettingsReader reader, string moniker, T fallback)
        where T : notnull
    {
        try
        {
            var result = reader.GetValue<T>(moniker, SettingReadOptions.NoRequirements);

            // 成功但值是 null 的情形理論上不存在，但回傳型別沒有排除它，
            // 一起當成「讀不到」處理比在下游到處防呆便宜。
            if (result.Outcome == SettingRetrievalOutcome.Success && result.Value is { } value)
            {
                return value;
            }

            SqlAssistDiagnostics.Write($"設定 {moniker} 回退為預設值：{result.Outcome}");
            return fallback;
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"讀取設定 {moniker} 失敗：{exception.Message}");
            return fallback;
        }
    }

    /// <summary>無法辨識的值一律當成預設值，而不是列舉的第一個成員。</summary>
    private static SqlPreviewMode ParsePreviewMode(string value, SqlPreviewMode fallback)
    {
        return value switch
        {
            "off" => SqlPreviewMode.Off,
            "delay" => SqlPreviewMode.Delay,
            "rightArrow" => SqlPreviewMode.RightArrow,
            _ => fallback
        };
    }

    private static SqlPreviewPlacement ParsePlacement(string value, SqlPreviewPlacement fallback)
    {
        return value switch
        {
            "beside" => SqlPreviewPlacement.Beside,
            "stacked" => SqlPreviewPlacement.Stacked,
            _ => fallback
        };
    }
}
