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
    private const string HeightProperty = "Height";

    private static WritableSettingsStore? _store;
    private static bool _storeResolved;

    public static double Width { get; private set; } = SqlAssistLimits.DefaultPreviewWidth;

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
    /// <param name="width">
    /// <c>null</c> 代表這次不更新寬度——上下擺放的寬度是編輯器決定的，
    /// 不是使用者拖出來的，寫回去等於用視窗寬度蓋掉他為側邊擺放調好的那一個值。
    /// </param>
    public static void Save(double? width, double height)
    {
        if (width is { } newWidth)
        {
            Width = SqlAssistLimits.ClampPreviewWidth(newWidth);
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
            store.SetInt32(Collection, HeightProperty, (int)Height);
        }
        catch (Exception exception)
        {
            // 記不住尺寸不影響這一次的使用，下次回到預設值即可。
            SqlAssistDiagnostics.WriteAlways($"儲存預覽視窗尺寸失敗：{exception.Message}");
        }
    }
}
