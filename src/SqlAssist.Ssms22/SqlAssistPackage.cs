using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(NoSolutionUiContextGuid, PackageAutoLoadFlags.BackgroundLoad)]
// 設定全部由 Unified Settings 提供：這個屬性在 pkgdef 寫下 SettingsManifests 項目，
// 殼層啟動時就會讀進註冊檔，不必等套件載入。
[ProvideSettingsManifest(PackageRelativeManifestFile = SettingsManifestFile)]
[Guid(PackageGuidString)]
public sealed class SqlAssistPackage : AsyncPackage
{
    public const string PackageGuidString = "b386e18d-f34b-4db4-a40d-b9092a31d89f";

    /// <summary>Unified Settings 的註冊檔，相對於擴充的安裝資料夾。</summary>
    private const string SettingsManifestFile = "SqlAssist.registration.json";

    /// <summary>版本一律由組件中繼資料取得，避免與 csproj 的 Version 脫節。</summary>
    internal static string PackageVersion =>
        typeof(SqlAssistPackage).Assembly.GetName().Version?.ToString() ?? "未知";

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

            // 命令的勾選狀態要靠設定回答，所以設定必須先接上。
            SqlAssistSettingsStore.Initialize(this);
            PreviewWindowState.Initialize(this);

            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

            if (commandService is null)
            {
                SqlAssistDiagnostics.WriteAlways("AsyncPackage 無法取得 OleMenuCommandService");
                return;
            }

            SqlAssistCommands.Register(this, commandService);
            SqlAssistRuntimeState.MarkPackageLoaded();
            SqlAssistDiagnostics.WriteAlways($"AsyncPackage {PackageVersion} 已載入，工具選單已註冊");
        }
        catch (Exception exception)
        {
            // 即使套件載入失敗，也要留下可由診斷腳本讀取的原因。
            SqlAssistDiagnostics.WriteAlways($"AsyncPackage 載入失敗：{exception}");
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SqlAssistSettingsStore.Shutdown();
        }

        base.Dispose(disposing);
    }
}
