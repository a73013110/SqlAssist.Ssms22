using System;
using Microsoft.Internal.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22;

namespace SqlAssist.Ssms22.Settings;

/// <summary>
/// 從 SSMS 的 Unified Settings 讀出一份 <see cref="SqlAssistSettings"/> 快照並保持最新。
/// </summary>
/// <remarks>
/// 這個類別只做平台那一半：拿服務、包成 <see cref="ISettingValueSource"/>、
/// 訂閱變更、快取結果。moniker 與屬性之間的對應、列舉解析與數值收斂
/// 都在 <see cref="SqlAssistSettingsReader"/>，那一半跑得起單元測試。
///
/// 為什麼要快取而不是每次都問 reader：建議來源的
/// <c>InitializeCompletion</c> 與輸入字元的處理常式都在按鍵路徑上，
/// 每按一次鍵去查十幾個 moniker 是不必要的成本。改成啟動時讀一次、
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
        if (serviceProvider is null)
        {
            return;
        }

        // 語言偏好的入口與 Unified Settings 是兩件事，先接上：即使
        // Unified Settings 缺席、整份設定回退成預設值，那份預設值仍然是
        // 「只使用 SqlAssist 的建議清單」，還是得推出去。
        NativeMemberList.Initialize(serviceProvider);

        if (_reader is null)
        {
            lock (SyncRoot)
            {
                if (_reader is null)
                {
                    // 設定讀不到不可以讓編輯器開不起來。
                    SqlAssistPlatformGuard.Run("初始化 Unified Settings", () =>
                    {
                        _manager = serviceProvider.GetService(typeof(SVsUnifiedSettingsManager))
                            as ISettingsManager;

                        if (_manager is null)
                        {
                            SqlAssistDiagnostics.WriteAlways(
                                "取不到 SVsUnifiedSettingsManager，SqlAssist 改用內建預設值執行");
                            return;
                        }

                        var reader = _manager.GetReader();
                        _current = SqlAssistSettingsReader.Read(new UnifiedSettingsSource(reader));
                        _reader = reader;

                        // 訂閱回呼可能來自任何執行緒；這裡只換掉一個 volatile 欄位，
                        // 讀取端拿到的永遠是完整的一份快照。
                        _subscription = reader.SubscribeToChanges(_ => Reload(), SqlAssistMonikers.All);
                        SqlAssistDiagnostics.WriteAlways("已接上 Unified Settings");
                    });
                }
            }
        }

        // 每一次都重套，不是只有第一次：這個方法在套件載入與每一個 SQL 編輯器
        // 建立時都會走到，而那正是「有人在外面把它改回去了」最可能被發現的時機。
        // 狀態相同時 ApplyFromSettings 不會寫入。
        NativeMemberList.ApplyFromSettings();
    }

    /// <summary>
    /// 套件卸載時解除訂閱；訂閱物件活到這裡為止。
    /// </summary>
    /// <remarks>
    /// 順便把 SSMS 的語言偏好還原。那是唯一一個寫在擴充之外的狀態，
    /// 而 SSMS 22 的設定 UI 沒有暴露它——不還原的話，解除安裝之後
    /// 內建清單就永遠不會再彈出來，而且使用者找不到地方改回去。
    /// 下一次啟動會在套件載入時重新套用，所以還原不會讓設定失效。
    /// </remarks>
    public static void Shutdown()
    {
        NativeMemberList.Restore();

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
        return SqlAssistPlatformGuard.Run(
            $"寫入設定 {moniker}",
            () => TrySetValueCore(moniker, value),
            fallback: false);
    }

    private static bool TrySetValueCore<T>(string moniker, T value)
        where T : notnull
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

    /// <summary>
    /// 讀取 SSMS 內建 T-SQL IntelliSense 的開關。
    /// </summary>
    /// <returns>讀不到時為 <c>null</c>；那代表 SSMS 沒有註冊這個設定，不代表它是關的。</returns>
    public static bool? TryGetNativeIntelliSenseEnabled()
    {
        if (_reader is not { } reader)
        {
            return null;
        }

        return SqlAssistPlatformGuard.Run<bool?>(
            "讀取 SSMS 內建 IntelliSense 狀態",
            () =>
            {
                var result = reader.GetValue<bool>(
                    SqlAssistMonikers.NativeIntelliSenseEnabled,
                    SettingReadOptions.NoRequirements);

                return result.Outcome == SettingRetrievalOutcome.Success ? result.Value : null;
            },
            fallback: null);
    }

    private static void Reload()
    {
        if (_reader is not { } reader)
        {
            return;
        }

        // 保留上一份可用的快照，總比切回預設值讓使用者的設定突然失效好。
        SqlAssistPlatformGuard.Run(
            "重新讀取設定",
            () => _current = SqlAssistSettingsReader.Read(new UnifiedSettingsSource(reader)));

        // 其餘設定放著等人來讀就好，只有這一個要推到擴充外面去。
        // 少了這一行，勾掉「只使用 SqlAssist 的建議清單」要重開 SSMS 才會生效。
        NativeMemberList.ApplyFromSettings();
    }

    /// <summary>
    /// 把 <see cref="ISettingsReader"/> 包成 <see cref="ISettingValueSource"/>。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="SettingReadOptions.NoRequirements"/> 而不是 <c>GetValueOrThrow</c>：
    /// 這條路徑在編輯器啟動與設定變更時跑，任何一個 moniker 出問題都不該
    /// 讓其餘十幾個跟著失效。記錄診斷訊息也留在這一層——Core 那半刻意不認識記錄器。
    /// </remarks>
    private sealed class UnifiedSettingsSource : ISettingValueSource
    {
        private readonly ISettingsReader _source;

        public UnifiedSettingsSource(ISettingsReader source) => _source = source;

        public bool TryGetValue<T>(string moniker, out T value)
            where T : notnull
        {
            // 「有沒有讀到」與「讀到什麼」得一起帶回來：bool 設定讀到 false 時，
            // 拿值本身當哨兵會把它誤判成沒讀到。
            var read = SqlAssistPlatformGuard.Run(
                $"讀取設定 {moniker}",
                () =>
                {
                    var result = _source.GetValue<T>(moniker, SettingReadOptions.NoRequirements);

                    // 成功但值是 null 的情形理論上不存在，但回傳型別沒有排除它，
                    // 一起當成「讀不到」處理比在下游到處防呆便宜。
                    if (result.Outcome == SettingRetrievalOutcome.Success && result.Value is { } actual)
                    {
                        return (Found: true, Value: actual);
                    }

                    SqlAssistDiagnostics.Write($"設定 {moniker} 回退為預設值：{result.Outcome}");
                    return (Found: false, Value: default(T)!);
                },
                fallback: (Found: false, Value: default(T)!));

            value = read.Value;
            return read.Found;
        }
    }
}
