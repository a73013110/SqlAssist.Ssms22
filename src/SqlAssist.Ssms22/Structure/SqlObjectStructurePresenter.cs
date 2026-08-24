using System;
using System.Threading.Tasks;
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
