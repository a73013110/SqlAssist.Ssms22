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
    private const string WidthProperty = "Width";
    private const string StackedWidthProperty = "StackedWidth";
    private const string HeightProperty = "Height";
    private const string BesideWidthProperty = "BesideWidth";
    private const string BesideHeightProperty = "BesideHeight";
    private const string StackedHeightProperty = "StackedHeight";
    private const string SchemaVersionProperty = "SchemaVersion";
    private const int CurrentSchemaVersion = 2;

    private static WritableSettingsStore? _store;
    private static bool _storeResolved;

    public static double BesideWidth { get; private set; } = SqlAssistLimits.DefaultPreviewWidth;

    public static double BesideHeight { get; private set; } = SqlAssistLimits.DefaultPreviewHeight;

    /// <summary>
    /// 使用者為上下擺放拖出的寬度；null 代表仍採用「延伸到編輯器右側」的自動寬度。
    /// </summary>
    public static double? StackedWidth { get; private set; }

    public static double StackedHeight { get; private set; } = SqlAssistLimits.DefaultPreviewHeight;

    /// <summary>某一種擺放方向記住的尺寸。</summary>
    public static PreviewPreferredSize Preferred(SqlPreviewPlacement placement) =>
        placement == SqlPreviewPlacement.Stacked
            ? new PreviewPreferredSize(StackedWidth, StackedHeight)
            : new PreviewPreferredSize(BesideWidth, BesideHeight);

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

        // 每一欄分開探測；單一損壞值只回退自己，不能讓其餘三個合法尺寸一起失效。
        var legacyWidth = ReadInt32(store, WidthProperty, (int)SqlAssistLimits.DefaultPreviewWidth);
        var legacyHeight = ReadInt32(store, HeightProperty, (int)SqlAssistLimits.DefaultPreviewHeight);
        BesideWidth = SqlAssistLimits.ClampPreviewWidth(
            ReadInt32(store, BesideWidthProperty, legacyWidth));
        BesideHeight = SqlAssistLimits.ClampPreviewHeight(
            ReadInt32(store, BesideHeightProperty, legacyHeight));
        StackedHeight = SqlAssistLimits.ClampPreviewHeight(
            ReadInt32(store, StackedHeightProperty, legacyHeight));

        // 0 是「尚未手動調過」的哨兵值；舊版沒有這個欄位時也會自然進入自動模式。
        var stackedWidth = ReadInt32(store, StackedWidthProperty, 0);
        StackedWidth = stackedWidth > 0
            ? SqlAssistLimits.ClampPreviewWidth(stackedWidth)
            : null;
    }

    /// <summary>
    /// 記下使用者拖出來的尺寸。
    /// </summary>
    /// <remarks>只傳入實際改動的軸；空間不足造成的有效尺寸不得污染偏好。</remarks>
    public static void Save(
        SqlPreviewPlacement placement,
        double? width,
        double? height)
    {
        if (placement == SqlPreviewPlacement.Stacked)
        {
            if (width is { } newStackedWidth)
            {
                StackedWidth = SqlAssistLimits.ClampPreviewWidth(newStackedWidth);
            }

            if (height is { } newStackedHeight)
            {
                StackedHeight = SqlAssistLimits.ClampPreviewHeight(newStackedHeight);
            }
        }
        else
        {
            if (width is { } newBesideWidth)
            {
                BesideWidth = SqlAssistLimits.ClampPreviewWidth(newBesideWidth);
            }

            if (height is { } newBesideHeight)
            {
                BesideHeight = SqlAssistLimits.ClampPreviewHeight(newBesideHeight);
            }
        }

        Persist();
    }

    /// <summary>恢復這一種擺放的預設尺寸；stacked 同時回到自動寬度。</summary>
    public static void Reset(SqlPreviewPlacement placement)
    {
        if (placement == SqlPreviewPlacement.Stacked)
        {
            StackedWidth = null;
            StackedHeight = SqlAssistLimits.DefaultPreviewHeight;
        }
        else
        {
            BesideWidth = SqlAssistLimits.DefaultPreviewWidth;
            BesideHeight = SqlAssistLimits.DefaultPreviewHeight;
        }

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
            store.SetInt32(Collection, BesideWidthProperty, (int)BesideWidth);
            store.SetInt32(Collection, BesideHeightProperty, (int)BesideHeight);
            store.SetInt32(Collection, StackedWidthProperty, (int)(StackedWidth ?? 0));
            store.SetInt32(Collection, StackedHeightProperty, (int)StackedHeight);

            // 保留舊欄位，使用者降回舊版時至少仍能沿用主要尺寸。
            store.SetInt32(Collection, WidthProperty, (int)BesideWidth);
            store.SetInt32(Collection, HeightProperty, (int)StackedHeight);
        });
    }

    private static int ReadInt32(WritableSettingsStore store, string property, int fallback) =>
        SqlAssistPlatformGuard.Probe(
            $"讀取預覽視窗尺寸 {property}",
            () => store.GetInt32(Collection, property, fallback),
            fallback);
}
