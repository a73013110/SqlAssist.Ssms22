using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Metadata;
using SqlAssist.Ssms22.Completion;

namespace SqlAssist.Ssms22.Structure;

/// <summary>
/// 開啟結構面板並切換到指定的物件。
/// </summary>
/// <remarks>
/// 提示視窗的連結、工具選單的命令都走這裡，兩條入口顯示出來的一定是同一個面板。
/// </remarks>
internal static class SqlObjectStructurePresenter
{
    /// <summary>顯示某個物件的結構；可以從任何執行緒呼叫。</summary>
    public static void Show(ITextView textView, SqlObjectInfo objectInfo, IServiceProvider serviceProvider)
    {
        if (objectInfo is null)
        {
            return;
        }

        // 提示視窗的連結不能等：使用者點下去，提示就要收起來。
        _ = ShowSafeAsync(textView, objectInfo, serviceProvider);
    }

    private static async Task ShowSafeAsync(
        ITextView textView,
        SqlObjectInfo objectInfo,
        IServiceProvider serviceProvider)
    {
        try
        {
            await ShowAsync(textView, objectInfo, serviceProvider);
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"開啟結構面板失敗：{exception}");
        }
    }

    /// <summary>
    /// 面板<b>已經開著</b>時，讓它跟著換到這個物件；沒開就什麼都不做。
    /// </summary>
    /// <remarks>
    /// 建議清單的選取每換一次就會呼叫一次。刻意不建立、也不喚起視窗：
    /// 使用者只是在清單裡上下移動，突然跳出一個工具視窗是打擾。
    /// 開著的人則是刻意把它擺在那裡對照的，那就讓它跟著看。
    /// </remarks>
    public static void FollowIfOpen(
        ITextView textView,
        SqlObjectInfo objectInfo,
        IServiceProvider serviceProvider)
    {
        if (objectInfo is null || textView is null)
        {
            return;
        }

        _ = FollowSafeAsync(textView, objectInfo, serviceProvider);
    }

    private static async Task FollowSafeAsync(
        ITextView textView,
        SqlObjectInfo objectInfo,
        IServiceProvider serviceProvider)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // 這裡不呼叫 GetPackageAsync：套件還沒載入就代表面板一定沒開。
            if (SqlAssistPackage.Instance is not { } package)
            {
                return;
            }

            var window = await package.FindToolWindowAsync(
                typeof(SqlObjectStructureWindow),
                id: 0,
                create: false,
                cancellationToken: package.DisposalToken) as SqlObjectStructureWindow;

            if (window?.Frame is not IVsWindowFrame frame || frame.IsVisible() != VSConstants.S_OK)
            {
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            window.Control.Follow(
                objectInfo,
                SqlCompletionServices.GetMetadataService(textView, serviceProvider));
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.Write($"結構面板跟隨選取失敗：{exception.Message}");
        }
    }

    private static async Task ShowAsync(
        ITextView textView,
        SqlObjectInfo objectInfo,
        IServiceProvider serviceProvider)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var package = await GetPackageAsync();

        if (package is null)
        {
            SqlAssistDiagnostics.WriteAlways("找不到 SqlAssist 套件實例，無法開啟結構面板");
            return;
        }

        var window = await package.ShowToolWindowAsync(
            typeof(SqlObjectStructureWindow),
            id: 0,
            create: true,
            cancellationToken: package.DisposalToken) as SqlObjectStructureWindow;

        if (window is null)
        {
            SqlAssistDiagnostics.WriteAlways("無法建立結構面板");
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        window.Control.Show(objectInfo, SqlCompletionServices.GetMetadataService(textView, serviceProvider));
    }

    /// <summary>
    /// 取得套件實例；還沒載入的話請 shell 載入它。
    /// </summary>
    /// <remarks>
    /// 套件設定為無方案時自動載入，正常情況下早就在了。但工具視窗必須由套件建立，
    /// 這裡不能假設「一定已經載入」——真的沒載入時使用者會看到什麼都沒發生。
    /// </remarks>
    private static async Task<SqlAssistPackage?> GetPackageAsync()
    {
        if (SqlAssistPackage.Instance is { } loaded)
        {
            return loaded;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (Package.GetGlobalService(typeof(SVsShell)) is IVsShell shell)
        {
            var packageGuid = new Guid(SqlAssistPackage.PackageGuidString);
            shell.LoadPackage(ref packageGuid, out _);
        }

        return SqlAssistPackage.Instance;
    }
}
