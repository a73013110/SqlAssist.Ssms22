using System;
using System.ComponentModel.Design;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Internal.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.Snippets;

namespace SqlAssist.Ssms22.Commands;

internal sealed class SqlAssistCommands
{
    private readonly SqlAssistPackage _package;
    private readonly OleMenuCommandService _commandService;

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

        AddToggleCommand(
            CommandIds.ToggleEnabled,
            SqlAssistMonikers.Enabled,
            () => SqlAssistSettingsStore.Current.Enabled,
            "SqlAssist");

        AddToggleCommand(
            CommandIds.ToggleSuggestions,
            SqlAssistMonikers.SuggestionsEnabled,
            () => SqlAssistSettingsStore.Current.SuggestionsEnabled,
            "即時建議");

        AddCommand(CommandIds.ShowObjectStructure, ShowObjectStructure);
        AddCommand(CommandIds.RefreshSuggestions, RefreshSuggestions);
        AddCommand(CommandIds.ManageSnippets, ManageSnippets);
        AddCommand(CommandIds.OpenSettings, OpenSettings);
        AddCommand(CommandIds.ShowDiagnostics, ShowDiagnostics);

        // 只出現在 Unified Settings 的設定頁上，不在任何選單裡。
        AddCommand(CommandIds.OpenDiagnosticsLog, OpenDiagnosticsLog);
    }

    private void AddToggleCommand(
        int commandId,
        string moniker,
        Func<bool> getChecked,
        string displayName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var menuCommand = new OleMenuCommand(
            (_, _) =>
            {
                var before = getChecked();
                var after = SqlAssistSettingsStore.Toggle(moniker, before);

                // 寫不進去時 Toggle 會回傳原值；紀錄要說實話，否則查問題時
                // 會看到「已切換」卻找不到任何行為改變。
                SqlAssistDiagnostics.WriteAlways(after == before
                    ? $"{displayName} 切換失敗，維持：{before}"
                    : $"{displayName} 已切換為：{after}");
            },
            new CommandID(CommandIds.CommandSet, commandId));

        menuCommand.BeforeQueryStatus += (_, _) =>
        {
            menuCommand.Checked = getChecked(); // 選單勾選狀態永遠反映 Unified Settings。
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

    /// <summary>
    /// 在浮動預覽視窗顯示游標所在的物件。
    /// </summary>
    /// <remarks>
    /// 平常的入口是建議清單的向右鍵與滑鼠停留提示裡的連結，
    /// 但前者要有清單、後者要求「滑鼠停留時顯示物件結構」是開著的。
    /// 這個命令讓預覽在任何設定下都還有一個入口。
    /// </remarks>
    private void ShowObjectStructure(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _ = ShowObjectStructureAsync();
    }

    private async Task ShowObjectStructureAsync()
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var textView = ActiveSqlEditor.Current;

            if (textView is null)
            {
                ShowMessage("請先把游標放進 SQL 查詢視窗。");
                return;
            }

            var caret = textView.Caret.Position.BufferPosition;
            var text = caret.Snapshot.GetText();
            var metadataService = SqlCompletionServices.GetMetadataService(textView, _package);

            // 使用者主動要求的路徑，等得起一次查詢。
            var location = await SqlObjectLocator.LocateAsync(
                metadataService,
                text,
                caret.Position,
                CancellationToken.None);

            if (location is null)
            {
                ShowMessage("游標處不是可辨識的資料庫物件。");
                return;
            }

            var anchor = caret.Snapshot.CreateTrackingSpan(
                new Microsoft.VisualStudio.Text.Span(
                    location.Reference.Start,
                    location.Reference.Length),
                Microsoft.VisualStudio.Text.SpanTrackingMode.EdgeInclusive);

            if (SqlStructurePreview.GetOrCreate(textView, _package) is { } preview)
            {
                preview.ShowAt(anchor, location.Object, metadataService);
            }
        }
        catch (Exception exception)
        {
            Report("開啟物件結構", exception);
        }
    }

    /// <summary>
    /// 工具選單命令的失敗處理：記錄完整例外，並讓使用者看見原因。
    /// </summary>
    /// <remarks>
    /// 刻意不走 <see cref="SqlAssistPlatformGuard"/>。那一族的意思是「這一輪安靜地
    /// 什麼都不做」，但使用者是自己按下這個選單項目的——什麼都沒發生等於故障。
    /// </remarks>
    private void Report(string operation, Exception exception)
    {
        SqlAssistDiagnostics.WriteAlways($"{operation}失敗：{exception}");
        ShowMessage($"{operation}失敗：{exception.Message}");
    }

    private void ShowMessage(string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VsShellUtilities.ShowMessageBox(
            _package,
            message,
            "SqlAssist",
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    private void ShowDiagnostics(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var settings = SqlAssistSettingsStore.Current;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "未知";

        var message =
            $"版本：{version}\r\n" +
            $"AsyncPackage：{FormatState(SqlAssistRuntimeState.PackageLoaded)}\r\n" +
            $"已建立 SQL 編輯器：{SqlAssistRuntimeState.TextViewCount}\r\n" +
            $"最後展開：{SqlAssistRuntimeState.LastExpansion}\r\n\r\n" +
            $"── 一般 ──\r\n" +
            $"啟用 SqlAssist：{FormatState(settings.Enabled)}\r\n" +
            $"輸入時關鍵字轉大寫：{FormatState(settings.UppercaseKeywordsOnType)}\r\n" +
            $"按 Tab 展開 SELECT *：{FormatState(settings.ExpandWildcardOnTab)}" +
            $"（{settings.WildcardLayout}）\r\n\r\n" +
            $"── 建議清單 ──\r\n" +
            $"自動彈出：{FormatState(settings.SuggestionsEnabled)}\r\n" +
            $"觸發字元數：{settings.TriggerAfterCharacters}\r\n" +
            $"程式碼片段：{FormatState(settings.IncludeSnippets)}\r\n" +
            $"資料庫物件與欄位：{FormatState(settings.IncludeDatabaseObjects)}\r\n" +
            $"分類篩選列：{FormatState(settings.ShowCategoryFilters)}\r\n" +
            $"補結構描述／方括號：{FormatState(settings.QualifyObjectNames)}／" +
            $"{FormatState(settings.UseSquareBrackets)}\r\n" +
            $"只使用 SqlAssist 的清單：{FormatState(settings.SuppressNativeMemberList)}" +
            $"（實際 {FormatNativeMemberList()}）\r\n" +
            $"SSMS 內建 IntelliSense：{FormatNativeIntelliSense()}\r\n\r\n" +
            $"── 物件結構 ──\r\n" +
            $"滑鼠停留提示：{FormatState(settings.HoverEnabled)}\r\n" +
            $"預覽時機：{settings.PreviewMode}（{settings.PreviewDelayMilliseconds} ms）\r\n" +
            $"預覽位置／字級：{settings.PreviewPlacement}／{settings.PreviewFontSize}\r\n" +
            $"預覽視窗尺寸：上下 {PreviewWindowState.StackedWidth?.ToString("F0") ?? "自動"}×{PreviewWindowState.StackedHeight:F0}；" +
            $"側邊 {PreviewWindowState.BesideWidth:F0}×{PreviewWindowState.BesideHeight:F0}\r\n\r\n" +
            $"── 診斷 ──\r\n" +
            $"詳細診斷記錄：{FormatState(settings.VerboseLogging)}\r\n" +
            $"診斷檔：{SqlAssistDiagnostics.LogPath}";

        VsShellUtilities.ShowMessageBox(
            _package,
            message,
            "SqlAssist 診斷狀態",
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    /// <summary>
    /// 開啟程式碼片段管理員。
    /// </summary>
    /// <remarks>
    /// 存檔之後不必手動重整建議清單：清單的候選是依 <c>SqlSnippetStore.Current</c>
    /// 的參考重建的，存檔換掉了那份參考，下一次按鍵就會拿到新的。
    /// </remarks>
    private void ManageSnippets(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            new SqlSnippetManagerWindow().ShowModal();
        }
        catch (Exception exception)
        {
            Report("開啟程式碼片段管理員", exception);
        }
    }

    /// <summary>開啟 Unified Settings 並定位到 SqlAssist 分類。</summary>
    private void OpenSettings(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            IServiceProvider serviceProvider = _package;

            if (serviceProvider.GetService(typeof(SVsUnifiedSettingsUiController))
                is IVsUnifiedSettingsUiController2 controller)
            {
                controller.ShowUnifiedSettingsDialog(new[] { SqlAssistMonikers.Category });
                return;
            }

            ShowMessage("無法開啟設定視窗，請改用「工具 → 選項」並搜尋 SqlAssist。");
        }
        catch (Exception exception)
        {
            Report("開啟設定視窗", exception);
        }
    }

    private void OpenDiagnosticsLog(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            SqlAssistDiagnostics.EnsureLogFile();
            VsShellUtilities.OpenDocument(_package, SqlAssistDiagnostics.LogPath);
        }
        catch (Exception exception)
        {
            Report("開啟診斷紀錄檔", exception);
        }
    }

    /// <remarks>
    /// 只清掉中繼資料快取就夠了：原生管線每次觸發都會重新問來源，
    /// 不需要另外去戳已經開著的清單。
    /// </remarks>
    private void RefreshSuggestions(object? sender, EventArgs eventArgs)
    {
        SqlMetadataService.InvalidateAll();
        SqlAssistDiagnostics.WriteAlways("使用者已要求重新整理建議");
    }

    private static string FormatState(bool enabled)
    {
        return enabled ? "啟用" : "停用";
    }

    /// <remarks>
    /// 建議的狀態是「啟用」：紅色錯誤波浪線與大綱都掛在它底下，關掉它等於
    /// 連錯誤檢查一起關掉，而互搶的那一半已經由
    /// <see cref="NativeMemberList"/> 單獨擋掉了。
    /// </remarks>
    private static string FormatNativeIntelliSense()
    {
        return SqlAssistSettingsStore.TryGetNativeIntelliSenseEnabled() switch
        {
            true => "啟用（錯誤波浪線與大綱可用）",
            false => "停用（沒有錯誤波浪線，建議開回來）",
            null => "讀不到"
        };
    }

    /// <summary>設定要的樣子與 SSMS 語言偏好實際的樣子分開顯示。</summary>
    /// <remarks>
    /// 兩者不一致就是「寫進去了但沒生效」，那是這個功能唯一會安靜失敗的方式。
    /// </remarks>
    private static string FormatNativeMemberList()
    {
        return NativeMemberList.TryGetSuppressed() switch
        {
            true => "內建清單已擋下",
            false => "內建清單仍會彈出",
            null => "讀不到"
        };
    }
}
