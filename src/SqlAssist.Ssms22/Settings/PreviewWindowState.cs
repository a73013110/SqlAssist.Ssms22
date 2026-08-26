using System;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Settings;
using SqlAssist.Core;

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

    private static WritableSettingsStore? _store;
    private static bool _storeResolved;

    public static double Width { get; private set; } = SqlAssistLimits.DefaultPreviewWidth;

    /// <summary>
    /// 使用者為上下擺放拖出的寬度；null 代表仍採用「延伸到編輯器右側」的自動寬度。
    /// </summary>
    public static double? StackedWidth { get; private set; }

    public static double Height { get; private set; } = SqlAssistLimits.DefaultPreviewHeight;

    /// <summary>
    /// 從存放區載入上次的尺寸。必須在 UI 執行緒上呼叫。
    /// </summary>
    public static void Initialize(IServiceProvider serviceProvider)
    {
        if (_storeResolved || serviceProvider is null)
        {
            return;
        }

        _storeResolved = true;

        try
        {
            var store = new ShellSettingsManager(serviceProvider)
                .GetWritableSettingsStore(SettingsScope.UserSettings);

            _store = store;

            if (!store.CollectionExists(Collection))
            {
                return;
            }

            Width = SqlAssistLimits.ClampPreviewWidth(
                store.GetInt32(Collection, WidthProperty, (int)SqlAssistLimits.DefaultPreviewWidth));

            // 0 是「尚未手動調過」的哨兵值；舊版沒有這個欄位時也會自然進入自動模式。
            var stackedWidth = store.GetInt32(Collection, StackedWidthProperty, 0);
            StackedWidth = stackedWidth > 0
                ? SqlAssistLimits.ClampPreviewWidth(stackedWidth)
                : null;

            Height = SqlAssistLimits.ClampPreviewHeight(
                store.GetInt32(Collection, HeightProperty, (int)SqlAssistLimits.DefaultPreviewHeight));
        }
        catch (Exception exception)
        {
            // 讀不到就用預設尺寸；預覽視窗開得出來比記得上次的大小重要。
            SqlAssistDiagnostics.WriteAlways($"讀取預覽視窗尺寸失敗：{exception.Message}");
        }
    }

    /// <summary>
    /// 記下使用者拖出來的尺寸。
    /// </summary>
    /// <param name="width"><c>null</c> 代表這次不更新側邊擺放寬度。</param>
    /// <param name="stackedWidth">
    /// <c>null</c> 代表這次沒有水平拖曳，不把自動寬度誤記成使用者偏好。
    /// </param>
    public static void Save(double? width, double? stackedWidth, double height)
    {
        if (width is { } newWidth)
        {
            Width = SqlAssistLimits.ClampPreviewWidth(newWidth);
        }

        if (stackedWidth is { } newStackedWidth)
        {
            StackedWidth = SqlAssistLimits.ClampPreviewWidth(newStackedWidth);
        }

        Height = SqlAssistLimits.ClampPreviewHeight(height);

        try
        {
            if (_store is not { } store)
            {
                return;
            }

            store.CreateCollection(Collection);
            store.SetInt32(Collection, WidthProperty, (int)Width);

            if (StackedWidth is { } savedStackedWidth)
            {
                store.SetInt32(Collection, StackedWidthProperty, (int)savedStackedWidth);
            }

            store.SetInt32(Collection, HeightProperty, (int)Height);
        }
        catch (Exception exception)
        {
            // 記不住尺寸不影響這一次的使用，下次回到預設值即可。
            SqlAssistDiagnostics.WriteAlways($"儲存預覽視窗尺寸失敗：{exception.Message}");
        }
    }
}
