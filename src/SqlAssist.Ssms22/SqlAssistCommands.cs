using System;
using System.ComponentModel.Design;
using System.Reflection;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SqlAssist.Core;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Options;

namespace SqlAssist.Ssms22;

internal sealed class SqlAssistCommands
{
    private readonly SqlAssistPackage _package;
    private readonly OleMenuCommandService _commandService;
    private readonly SettingsService _settings = SettingsService.Default;

    private SqlAssistCommands(SqlAssistPackage package, OleMenuCommandService commandService)
    {
        _package = package;
        _commandService = commandService;
    }

    public static void Register(SqlAssistPackage package, OleMenuCommandService commandService)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        new SqlAssistCommands(package, commandService).RegisterCommands();
    }

    private void RegisterCommands()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _settings.EnsureSettingsFile();

        AddToggleCommand(
            CommandIds.ToggleEnabled,
            () => _settings.GetSnapshot().Enabled,
            () => _settings.ToggleEnabled(),
            "SqlAssist");

        AddToggleCommand(
            CommandIds.ToggleSuggestions,
            () => _settings.GetSnapshot().Suggestions.Enabled,
            () => _settings.ToggleSuggestions(),
            "即時建議");

        AddFeatureToggle(CommandIds.ToggleTabExpansion, SqlAssistFeature.TabExpansion, "Tab 快捷展開");
        AddFeatureToggle(CommandIds.ToggleKeywordUppercase, SqlAssistFeature.KeywordUppercase, "關鍵字轉大寫");
        AddFeatureToggle(CommandIds.ToggleObjectPicker, SqlAssistFeature.ObjectPicker, "Procedure／Function 選擇器");
        AddFeatureToggle(CommandIds.ToggleResultGridCommands, SqlAssistFeature.ResultGridCommands, "結果格命令");

        AddToggleCommand(
            CommandIds.ToggleAsyncCompletionProbe,
            () => _settings.GetSnapshot().AsyncCompletionProbe,
            () => _settings.ToggleAsyncCompletionProbe(),
            "非同步 IntelliSense 探測");

        AddCommand(CommandIds.ShowDiagnostics, ShowDiagnostics);
        AddCommand(CommandIds.RefreshSuggestions, RefreshSuggestions);
        AddCommand(CommandIds.OpenSettings, OpenSettings);
        AddCommand(CommandIds.EditSettingsFile, EditSettingsFile);
    }

    private void AddFeatureToggle(int commandId, SqlAssistFeature feature, string displayName)
    {
        AddToggleCommand(
            commandId,
            () => _settings.GetSnapshot().Features.Get(feature),
            () => _settings.ToggleFeature(feature),
            displayName);
    }

    private void AddToggleCommand(
        int commandId,
        Func<bool> getChecked,
        Func<bool> toggle,
        string displayName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var menuCommand = new OleMenuCommand(
            (_, _) =>
            {
                var enabled = toggle();
                SqlAssistDiagnostics.WriteAlways($"{displayName} 已切換為：{enabled}");
            },
            new CommandID(CommandIds.CommandSet, commandId));

        menuCommand.BeforeQueryStatus += (_, _) =>
        {
            menuCommand.Checked = getChecked(); // 選單勾選狀態永遠反映 settings.json。
            menuCommand.Enabled = true;
            menuCommand.Visible = true;
        };

        _commandService.AddCommand(menuCommand);
    }

    private void AddCommand(int commandId, EventHandler handler)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _commandService.AddCommand(new OleMenuCommand(
            handler,
            new CommandID(CommandIds.CommandSet, commandId)));
    }

    private void ShowDiagnostics(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var settings = _settings.GetSnapshot();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "未知";
        var loadError = string.IsNullOrWhiteSpace(_settings.LastLoadError)
            ? "無"
            : _settings.LastLoadError;

        var message =
            $"版本：{version}\r\n" +
            $"AsyncPackage：{FormatState(SqlAssistRuntimeState.PackageLoaded)}\r\n" +
            $"已建立 SQL 編輯器：{SqlAssistRuntimeState.TextViewCount}\r\n" +
            $"收到 Tab 次數：{SqlAssistRuntimeState.TabCount}\r\n" +
            $"最後 Tab：{SqlAssistRuntimeState.LastTabSource}\r\n" +
            $"最後展開：{SqlAssistRuntimeState.LastExpansion}\r\n\r\n" +
            $"SqlAssist：{FormatState(settings.Enabled)}\r\n" +
            $"即時建議：{FormatState(settings.Suggestions.Enabled)}\r\n" +
            $"觸發字元數：{settings.Suggestions.TriggerAfterCharacters}\r\n" +
            $"預覽窗格：{FormatState(settings.Suggestions.ShowPreview)}\r\n" +
            $"Tab 快捷展開：{FormatState(settings.Features.TabExpansion)}\r\n" +
            $"關鍵字轉大寫：{FormatState(settings.Features.KeywordUppercase)}\r\n" +
            $"資料庫物件建議：{FormatState(settings.Features.ObjectPicker)}\r\n" +
            $"結果格命令設定：{FormatState(settings.Features.ResultGridCommands)}（功能開發中）\r\n" +
            $"詳細診斷記錄：{FormatState(settings.DiagnosticsEnabled)}\r\n\r\n" +
            $"── 非同步 IntelliSense 探測 ──\r\n" +
            $"探測模式：{FormatState(settings.AsyncCompletionProbe)}\r\n" +
            AsyncCompletionProbe.BuildReport() + "\r\n" +
            $"設定檔：{_settings.SettingsPath}\r\n" +
            $"診斷檔：{SqlAssistDiagnostics.LogPath}\r\n" +
            $"設定載入錯誤：{loadError}";

        VsShellUtilities.ShowMessageBox(
            _package,
            message,
            "SqlAssist 診斷狀態",
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    private void OpenSettings(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _package.ShowOptionPage(typeof(GeneralOptionsPage)); // 開啟「工具 → 選項 → SqlAssist」。
    }

    private void EditSettingsFile(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _settings.EnsureSettingsFile();
        VsShellUtilities.OpenDocument(_package, _settings.SettingsPath); // 進階設定仍可直接改 JSON。
    }

    private void RefreshSuggestions(object? sender, EventArgs eventArgs)
    {
        SqlMetadataService.InvalidateAll();
        SuggestionRefreshBroker.RequestRefresh();
        SqlAssistDiagnostics.WriteAlways("使用者已要求重新整理即時建議");
    }

    private static string FormatState(bool enabled)
    {
        return enabled ? "啟用" : "停用";
    }
}
