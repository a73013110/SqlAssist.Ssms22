using System;
using System.ComponentModel.Design;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Ssms22.Commands;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
// 版號一變，殼層下次載入就重建命令表快取。新增命令、選單項目或鍵繫結時**一定**要
// 加一：不加的話換掉 DLL 也沒有用，殼層仍在用舊的命令表——症狀是新的選單項目不出現、
// 新綁的鍵沒反應，而且沒有任何錯誤。與 MEF 快取是同一類的坑。
[ProvideMenuResource("Menus.ctmenu", 14)]
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

    /// <summary>版本一律由 NBGV 寫進組件的中繼資料取得。</summary>
    internal static SqlAssistBuildVersion BuildVersion { get; } = CreateBuildVersion();

    internal static string PackageVersion => BuildVersion.DisplayVersion;

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
            VsThemeBrushes.Initialize();

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
            SqlAssistRuntimeState.MarkPackageReady();
            SqlAssistDiagnostics.WriteAlways($"AsyncPackage {PackageVersion} 已載入，工具選單已註冊");
        }
        catch (Exception exception)
        {
            // 即使套件載入失敗，也要留下可由診斷腳本讀取的原因。
            // 不走 SqlAssistPlatformGuard：那一族會吞掉例外，而殼層要靠它知道
            // 這個套件沒載入成功；記錄完仍然重擲。
            SqlAssistDiagnostics.WriteAlways($"AsyncPackage 載入失敗：{exception}");
            throw;
        }
    }

    private static SqlAssistBuildVersion CreateBuildVersion()
    {
        var assembly = typeof(SqlAssistPackage).Assembly;

        // AssemblyVersion 為了二進位相容固定在 0.14.0.0；真正每次建置都會變的是
        // InformationalVersion。把前者拿來顯示，patch 看起來就會永遠是零。
        return SqlAssistBuildVersion.Create(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version,
            assembly.GetName().Version?.ToString());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SqlAssistSettingsStore.Shutdown();
            VsThemeBrushes.Shutdown();
        }

        base.Dispose(disposing);
    }
}
