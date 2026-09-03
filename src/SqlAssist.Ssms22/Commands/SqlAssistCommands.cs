using System;
using System.ComponentModel.Design;
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
using SqlAssist.Ssms22.ResultGrid;
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

        // 這三個命令都有全域鍵繫結（見 Menus.vsct），因此狀態必須跟著編輯器走；
        // 回報停用才不會在 SqlAssist 停用或沒有 SQL 編輯器時攔走殼層原本的按鍵行為。
        AddCommand(
            CommandIds.GoToDefinition,
            GoToDefinition,
            () => SqlAssistSettingsStore.Current.Enabled && ActiveSqlEditor.Current is not null);

        AddCommand(
            CommandIds.ShowObjectStructure,
            ShowObjectStructure,
            () => SqlAssistSettingsStore.Current.Enabled && ActiveSqlEditor.Current is not null);
        AddCommand(
            CommandIds.RefreshSuggestions,
            RefreshSuggestions,
            () => SqlAssistSettingsStore.Current.Enabled && ActiveSqlEditor.Current is not null);
        AddCommand(CommandIds.ManageSnippets, ManageSnippets);
        AddCommand(CommandIds.OpenSettings, OpenSettings);
        AddCommand(CommandIds.ShowDiagnostics, ShowAboutAndDiagnostics);

        // 只出現在 Unified Settings 的設定頁上，不在任何選單裡。
        AddCommand(CommandIds.OpenDiagnosticsLog, OpenDiagnosticsLog);

        // 結果格線的右鍵選單。狀態由 ResultGridActions 回答：找不到格線就停用，
        // 但仍然看得見——使用者因此知道這個功能存在，只是現在沒有東西可以做。

        // 標頭是標籤不是命令：沒有處理常式，也永遠停用。
        AddCommand(CommandIds.ResultGridHeader, (_, _) => { }, isEnabled: () => false);

        AddCommand(
            CommandIds.ResultGridTempTable,
            (_, _) => ResultGridActions.CreateTempTableScript(_package),
            ResultGridActions.IsAvailable);

        AddCommand(
            CommandIds.ResultGridInPredicate,
            (_, _) => ResultGridActions.CopyInPredicate(_package),
            ResultGridActions.IsAvailable);

        AddCommand(
            CommandIds.ResultGridProfile,
            (_, _) => ResultGridActions.ShowProfile(_package),
            ResultGridActions.IsAvailable);

        AddCommand(
            CommandIds.ResultGridCell,
            (_, _) => ResultGridActions.ShowCell(_package),
            ResultGridActions.IsAvailable);

        AddCommand(
            CommandIds.ResultGridMarkdown,
            (_, _) => ResultGridActions.CopyMarkdownTable(_package),
            ResultGridActions.IsAvailable);

        // 探測只在「詳細記錄」打開時出現。它是診斷工具，不是功能。
        AddCommand(
            CommandIds.ProbeResultGrid,
            ProbeResultGrid,
            isVisible: () => SqlAssistSettingsStore.Current.VerboseLogging);
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

    /// <param name="isEnabled">
    /// 命令什麼時候可用；省略代表永遠可用。綁了鍵的命令一定要給——殼層是先問過
    /// 狀態才派送的，而回報可用卻什麼都不做，跟按鍵沒反應在使用者眼裡是同一件事。
    /// </param>
    /// <param name="isVisible">
    /// 命令什麼時候出現在選單上；省略代表永遠出現。
    /// </param>
    /// <remarks>
    /// 停用與隱藏是兩件事，不要拿其中一個代替另一個。停用的項目仍然看得見，
    /// 使用者因此知道功能存在、只是現在用不上；隱藏留給「這個使用者根本不該
    /// 看到它」的那一種，目前只有診斷用的探測。
    /// </remarks>
    private void AddCommand(
        int commandId,
        EventHandler handler,
        Func<bool>? isEnabled = null,
        Func<bool>? isVisible = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var menuCommand = new OleMenuCommand(
            handler,
            new CommandID(CommandIds.CommandSet, commandId));

        if (isEnabled is not null || isVisible is not null)
        {
            menuCommand.BeforeQueryStatus += (_, _) =>
            {
                if (isVisible is not null)
                {
                    menuCommand.Visible = SqlAssistPlatformGuard.Run(
                        "查詢命令可見度",
                        isVisible,
                        fallback: false);
                }

                if (isEnabled is not null)
                {
                    menuCommand.Enabled = SqlAssistPlatformGuard.Run(
                        "查詢命令狀態",
                        isEnabled,
                        fallback: false);
                }
            };
        }

        _commandService.AddCommand(menuCommand);
    }

    /// <summary>
    /// 把游標處的物件定義開進新的查詢視窗。
    /// </summary>
    /// <remarks>
    /// 與 F12 是同一份實作（<see cref="SqlDefinitionOpener"/>）。
    ///
    /// 回饋一律走狀態列，<b>不用對話框</b>——這個命令綁著 F12，而一個要按確定才
    /// 消失的視窗出現在按鍵路徑上，比沒有反應更糟。其他工具選單命令維持對話框，
    /// 它們都不在按鍵路徑上。
    /// </remarks>
    private void GoToDefinition(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            SqlAssistDiagnostics.Write("移至定義命令抵達 SqlAssist（命令表）");

            // BeforeQueryStatus 已經擋掉這兩種，但殼層不保證每一次派送前都問過狀態。
            if (!SqlAssistSettingsStore.Current.Enabled)
            {
                SqlAssistStatusBar.Show(_package, "SqlAssist 目前已停用。");
                return;
            }

            if (ActiveSqlEditor.Current is not { } textView)
            {
                SqlAssistStatusBar.Show(_package, "請先把游標放進 SQL 查詢視窗。");
                return;
            }

            if (!SqlCompletionServices
                    .GetDefinitionOpener(textView, _package)
                    .TryBegin(textView.Caret.Position.BufferPosition))
            {
                SqlAssistStatusBar.Show(_package, "游標處不是可辨識的資料庫物件。");
            }
        }
        catch (Exception exception)
        {
            // 同上：這條路徑綁著按鍵，例外也走狀態列。
            SqlAssistDiagnostics.WriteAlways($"開啟物件定義失敗：{exception}");
            SqlAssistStatusBar.Show(_package, $"開啟物件定義失敗：{exception.Message}");
        }
    }

    /// <summary>
    /// 在浮動預覽視窗顯示游標所在的物件。
    /// </summary>
    /// <remarks>
    /// 平常的入口是建議清單的向右鍵與滑鼠停留提示裡的連結，
    /// 但前者要有清單、後者要求「滑鼠停留時顯示物件結構」是開著的。
    /// 這個命令讓預覽在任何設定下都還有一個入口，並由 Ctrl+F12 直接呼叫。
    /// 回饋走狀態列而不是對話框，避免快捷鍵路徑打斷編輯。
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
            SqlAssistDiagnostics.WriteAlways("顯示物件結構命令抵達 SqlAssist（命令表）");

            // BeforeQueryStatus 通常會擋掉，但殼層不保證每一次派送前都問過狀態。
            if (!SqlAssistSettingsStore.Current.Enabled)
            {
                SqlAssistStatusBar.Show(_package, "SqlAssist 目前已停用。");
                return;
            }

            var textView = ActiveSqlEditor.Current;

            if (textView is null)
            {
                SqlAssistStatusBar.Show(_package, "請先把游標放進 SQL 查詢視窗。");
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
                SqlAssistStatusBar.Show(_package, "游標處不是可辨識的資料庫物件。");
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
                return;
            }

            SqlAssistStatusBar.Show(_package, "查詢視窗已關閉，無法顯示物件結構。");
        }
        catch (Exception exception)
        {
            // 這條路徑綁著按鍵，失敗必須可見，但不應用對話框打斷編輯。
            SqlAssistDiagnostics.WriteAlways($"開啟物件結構失敗：{exception}");
            SqlAssistStatusBar.Show(_package, $"開啟物件結構失敗：{exception.Message}");
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

    /// <summary>
    /// 跑一次結果格線探測，報告寫進診斷紀錄檔。
    /// </summary>
    /// <remarks>
    /// 這是診斷工具而不是功能，只在「詳細記錄」打開時出現。SSMS 換版之後，
    /// 結果格線的命令會安靜地整組失效——那時候要先問出格線還在不在、
    /// 方法還叫不叫這個名字。
    ///
    /// 回饋走對話框：這個命令不在按鍵路徑上，而「按了沒反應」正是它要排除的失敗。
    /// </remarks>
    private void ProbeResultGrid(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var report = SqlAssistResultGridProbe.Run();
            var summary = Array.Find(
                report.Split('\n'),
                line => line.StartsWith("找到格線數量", StringComparison.Ordinal))
                ?.Trim() ?? "（報告裡沒有格線數量那一行）";

            ShowMessage($"結果格線探測完成。{summary}。完整報告已寫入診斷紀錄檔。");
        }
        catch (Exception exception)
        {
            Report("探測結果格線", exception);
        }
    }

    private void ShowAboutAndDiagnostics(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var snapshot = SqlAssistDiagnosticSnapshotFactory.Create();
            new SqlAssistAboutWindow(snapshot, TryOpenSettings, OpenDiagnosticsLogCore).ShowModal();
        }
        catch (Exception exception)
        {
            Report("開啟關於與診斷", exception);
        }
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
            if (TryOpenSettings())
            {
                return;
            }

            ShowMessage("無法開啟設定視窗，請改用「工具 → 選項」並搜尋 SqlAssist。");
        }
        catch (Exception exception)
        {
            Report("開啟設定視窗", exception);
        }
    }

    private bool TryOpenSettings()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        IServiceProvider serviceProvider = _package;

        if (serviceProvider.GetService(typeof(SVsUnifiedSettingsUiController))
            is not IVsUnifiedSettingsUiController2 controller)
        {
            return false;
        }

        controller.ShowUnifiedSettingsDialog(new[] { SqlAssistMonikers.Category });
        return true;
    }

    private void OpenDiagnosticsLog(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            OpenDiagnosticsLogCore();
        }
        catch (Exception exception)
        {
            Report("開啟診斷紀錄檔", exception);
        }
    }

    private void OpenDiagnosticsLogCore()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SqlAssistDiagnostics.EnsureLogFile();
        VsShellUtilities.OpenDocument(_package, SqlAssistDiagnostics.LogPath);
    }

    /// <remarks>
    /// 只清掉中繼資料快取就夠了：原生管線每次觸發都會重新問來源，
    /// 不需要另外去戳已經開著的清單。
    /// </remarks>
    private void RefreshSuggestions(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            SqlMetadataService.InvalidateAll();
            SqlAssistDiagnostics.WriteAlways("使用者已要求重新整理建議");
            SqlAssistStatusBar.Show(
                _package,
                "建議快取已清除；下次開啟建議清單時會重新讀取資料庫。");
        }
        catch (Exception exception)
        {
            // 這條路徑綁著按鍵，失敗必須可見，但不應用對話框打斷編輯。
            SqlAssistDiagnostics.WriteAlways($"重新整理建議失敗：{exception}");
            SqlAssistStatusBar.Show(_package, $"重新整理建議失敗：{exception.Message}");
        }
    }
}
