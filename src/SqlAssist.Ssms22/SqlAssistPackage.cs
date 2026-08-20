using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace SqlAssist.Ssms22;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(NoSolutionUiContextGuid, PackageAutoLoadFlags.BackgroundLoad)]
[Guid(PackageGuidString)]
public sealed class SqlAssistPackage : AsyncPackage
{
    public const string PackageGuidString = "b386e18d-f34b-4db4-a40d-b9092a31d89f";
    private const string NoSolutionUiContextGuid = "adfc4e64-0397-11d1-9f4e-00a0c911004f";

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        try
        {
            // SSMS 啟動且沒有方案時自動載入，確保工具選單的命令處理器已完成註冊。
            await base.InitializeAsync(cancellationToken, progress);
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

            if (commandService is null)
            {
                SqlAssistDiagnostics.WriteAlways("AsyncPackage 無法取得 OleMenuCommandService");
                return;
            }

            SqlAssistCommands.Register(this, commandService);
            SqlAssistRuntimeState.MarkPackageLoaded();
            SqlAssistDiagnostics.WriteAlways("AsyncPackage 0.4.1 已載入，工具選單已註冊");
        }
        catch (Exception exception)
        {
            // 即使套件載入失敗，也要留下可由診斷腳本讀取的原因。
            SqlAssistDiagnostics.WriteAlways($"AsyncPackage 載入失敗：{exception}");
            throw;
        }
    }
}
