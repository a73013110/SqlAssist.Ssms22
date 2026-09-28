using System;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Settings;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22;

namespace SqlAssist.Ssms22.Settings;

/// <summary>
/// 結構預覽視窗被拖出來的尺寸。
/// </summary>
/// <remarks>
/// 這是視窗狀態，不是偏好，所以刻意不放進 Unified Settings：
/// 那份設定是使用者刻意調整、會跟著設定漫遊同步、而且每次提交都會廣播
/// 變更通知的東西——拖曳握把一次寫一次顯然不屬於那一類。
/// VS 的 <see cref="WritableSettingsStore"/> 正是為這種 UI 狀態準備的。
/// </remarks>
internal static class PreviewWindowState
{
    private const string Collection = @"SqlAssist\Preview";

    // 上下擺放已是唯一的擺放；沿用 v2 的鍵名，升級時不必搬資料。
    private const string WidthProperty = "StackedWidth";
    private const string HeightProperty = "StackedHeight";

    /// <summary>v1 不分擺放的高度；還沒有 <see cref="HeightProperty"/> 時從這裡接。</summary>
    private const string LegacyHeightProperty = "Height";

    private const string SchemaVersionProperty = "SchemaVersion";
    private const int CurrentSchemaVersion = 3;

    /// <summary>已經沒有人讀的鍵：v1 的寬高與 v2 側邊擺放那一組。升到 v3 時刪掉一次。</summary>
    private static readonly string[] ObsoleteProperties = { "Width", LegacyHeightProperty, "BesideWidth", "BesideHeight" };

    private static WritableSettingsStore? _store;
    private static bool _storeResolved;

    public static PreviewPreferredSize Preferred { get; private set; } = PreviewPreferredSize.Default;

    /// <summary>
    /// 從存放區載入上次的尺寸。必須在 UI 執行緒上呼叫。
    /// </summary>
    public static void Initialize(IServiceProvider serviceProvider)
    {
        if (_storeResolved || serviceProvider is null)
        {
            return;
        }

        // 讀不到就用預設尺寸；預覽視窗開得出來比記得上次的大小重要。
        var store = SqlAssistPlatformGuard.Probe<WritableSettingsStore?>(
            "取得預覽視窗尺寸存放區",
            () => new ShellSettingsManager(serviceProvider)
                .GetWritableSettingsStore(SettingsScope.UserSettings),
            fallback: null);
        if (store is null)
        {
            // 套件初始化早期服務可能尚未就緒；不要把一次暫時失敗變成整個工作階段不再重試。
            return;
        }

        _store = store;
        _storeResolved = true;

        var collectionExists = SqlAssistPlatformGuard.Probe(
            "確認預覽視窗尺寸存放區",
            () => store.CollectionExists(Collection),
            fallback: false);
        if (!collectionExists)
        {
            return;
        }

        // 每一欄分開探測；一個損壞值只回退自己。
        var legacyHeight = ReadInt32(store, LegacyHeightProperty, (int)SqlAssistLimits.DefaultPreviewHeight);
        var height = SqlAssistLimits.ClampPreviewHeight(ReadInt32(store, HeightProperty, legacyHeight));

        // 0 是「尚未手動調過」的哨兵值；舊版沒有這個欄位時也會自然進入自動寬度。
        var width = ReadInt32(store, WidthProperty, 0);
        Preferred = new PreviewPreferredSize(
            width > 0 ? SqlAssistLimits.ClampPreviewWidth(width) : null,
            height);

        if (ReadInt32(store, SchemaVersionProperty, 0) < CurrentSchemaVersion)
        {
            Persist();
        }
    }

    /// <summary>
    /// 記下使用者拖出來的尺寸。
    /// </summary>
    /// <remarks>只傳入真的拖過的軸；沒拖的那一軸留著原本的值（包括自動寬度）。</remarks>
    public static void Save(double? width, double? height)
    {
        Preferred = new PreviewPreferredSize(
            width is { } newWidth ? SqlAssistLimits.ClampPreviewWidth(newWidth) : Preferred.Width,
            height is { } newHeight ? SqlAssistLimits.ClampPreviewHeight(newHeight) : Preferred.Height);
        Persist();
    }

    /// <summary>回到預設尺寸，寬度回到自動延伸。</summary>
    public static void Reset()
    {
        Preferred = PreviewPreferredSize.Default;
        Persist();
    }

    private static void Persist()
    {
        if (_store is not { } store)
        {
            return;
        }

        // 記不住尺寸不影響這一次的使用，下次回到預設值即可。
        SqlAssistPlatformGuard.Run("儲存預覽視窗尺寸", () =>
        {
            store.CreateCollection(Collection);
            store.SetInt32(Collection, SchemaVersionProperty, CurrentSchemaVersion);
            store.SetInt32(Collection, WidthProperty, (int)(Preferred.Width ?? 0));
            store.SetInt32(Collection, HeightProperty, (int)Preferred.Height);
            foreach (var property in ObsoleteProperties)
            {
                if (store.PropertyExists(Collection, property))
                {
                    store.DeleteProperty(Collection, property);
                }
            }
        });
    }

    private static int ReadInt32(WritableSettingsStore store, string property, int fallback) =>
        SqlAssistPlatformGuard.Probe(
            $"讀取預覽視窗尺寸 {property}",
            () => store.GetInt32(Collection, property, fallback),
            fallback);
}
