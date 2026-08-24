using System;
using System.ComponentModel.Design;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Ssms22.Options;
using SqlAssist.Ssms22.Structure;

namespace SqlAssist.Ssms22;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(NoSolutionUiContextGuid, PackageAutoLoadFlags.BackgroundLoad)]
// 設定必須出現在「工具 → 選項」，那才是使用者尋找擴充設定的地方；
// 工具選單的即時開關只是捷徑，不能是唯一入口。
[ProvideOptionPage(
    typeof(GeneralOptionsPage),
    OptionsCategory,
    "一般",
    categoryResourceID: 0,
    pageNameResourceID: 0,
    supportsAutomation: true)]
[ProvideOptionPage(
    typeof(SuggestionsOptionsPage),
    OptionsCategory,
    "建議清單",
    categoryResourceID: 0,
    pageNameResourceID: 0,
    supportsAutomation: true)]
// 結構面板由套件建立，必須在這裡註冊，否則 ShowToolWindowAsync 找不到它。
[ProvideToolWindow(typeof(SqlObjectStructureWindow), Style = VsDockStyle.Tabbed, Orientation = ToolWindowOrientation.Right)]
[Guid(PackageGuidString)]
public sealed class SqlAssistPackage : AsyncPackage
{
    public const string PackageGuidString = "b386e18d-f34b-4db4-a40d-b9092a31d89f";

    /// <summary>已載入的套件實例；結構面板需要它才能建立工具視窗。</summary>
    internal static SqlAssistPackage? Instance { get; private set; }

    /// <summary>「工具 → 選項」底下的分類名稱。</summary>
    public const string OptionsCategory = "SqlAssist";

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

            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

            if (commandService is null)
            {
                SqlAssistDiagnostics.WriteAlways("AsyncPackage 無法取得 OleMenuCommandService");
                return;
            }

            Instance = this;
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
}
