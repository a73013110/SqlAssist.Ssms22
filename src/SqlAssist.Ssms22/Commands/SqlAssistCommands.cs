using System;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Windows.Media;
using Microsoft.Internal.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Notifications;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.Search;
using SqlAssist.Ssms22.SqlMemory;
using SqlAssist.Ssms22.ResultGrid;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.Snippets;
using SqlAssist.Ssms22.UI;

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
        // 全域鍵繫結（Ctrl+Alt+S）、殼層命令濾鏡與右鍵選單都要問狀態；沒有選取時
        // 回報停用，右鍵選單上那一項就是灰的，Ctrl+Alt+S 也照常落回殼層。
        AddCommand(
            CommandIds.SurroundWith,
            SurroundWith,
            SqlSnippetSurroundAction.IsAvailable);
        AddCommand(
            CommandIds.SurroundWithFromTools,
            SurroundWith,
            SqlSnippetSurroundAction.IsAvailable);

        // 右鍵與工具選單使用不同的 VSCT ID 才能有不同圖示，但共用執行與狀態邏輯。
        // SQL Memory 關著或沒有東西可收就變灰，不讓使用者按下去才知道。
        AddCommand(
            CommandIds.AddToFavorites,
            AddToFavorites,
            SqlMemoryFavoriteAction.IsAvailable);
        AddCommand(
            CommandIds.AddToFavoritesFromTools,
            AddToFavorites,
            SqlMemoryFavoriteAction.IsAvailable);

        AddCommand(CommandIds.ManageSnippets, ManageSnippets);
        AddCommand(CommandIds.OpenSettings, OpenSettings);
        AddCommand(CommandIds.ShowSqlHistory, (_, _) => SqlMemoryToolWindow.Show(_package));
        AddCommand(CommandIds.ShowSqlFavorites, (_, _) => SqlMemoryToolWindow.Show(_package, SqlMemoryPage.Favorites));
        AddCommand(CommandIds.ShowSqlMemoryUsage, (_, _) => SqlMemoryToolWindow.Show(_package, SqlMemoryPage.Usage));
        // 永遠可用：沒有查詢視窗時工具窗自己說「尚未連線」，做成灰的會讓人以為功能壞了。
        // Show 內部不走 Guard，開不起來會跳訊息方塊——使用者是自己點選單的。
        AddCommand(CommandIds.ShowSqlSearch, (_, _) => SqlSearchToolWindow.Show(_package));
        AddCommand(CommandIds.ShowDiagnostics, ShowAboutAndDiagnostics);
        // 問一次要幾秒，連按只送出一次；結論走通知，所以不必等視窗。
        AddCommand(CommandIds.CheckForUpdates,
            (_, _) => SqlAssistUpdateCheckCommand.Execute(_package),
            () => !SqlAssistUpdateCheckCommand.IsRunning);
        AddCommand(CommandIds.FocusNotifications, FocusNotifications);

        // 只出現在 Unified Settings 的設定頁上，不在任何選單裡。
        AddCommand(CommandIds.OpenDiagnosticsLog, OpenDiagnosticsLog);
        // 設定頁的 SQL Memory 分類只有這一個出口；整理、壓縮與備份都在用量分頁上。
        AddCommand(CommandIds.ShowSqlMemoryUsageFromSettings,
            (_, _) => SqlMemoryToolWindow.Show(_package, SqlMemoryPage.Usage));
        AddColorCommand(CommandIds.PickBlockAccent, SqlAssistMonikers.BlockAccentColor, s => s.BlockAccentColor, ThemeBrush.AccentBorder);
        AddColorCommand(CommandIds.PickBlockKeywordForeground, SqlAssistMonikers.BlockKeywordForeground, s => s.BlockKeywordForeground, ThemeBrush.BlockKeywordForeground);
        AddColorCommand(CommandIds.PickBlockKeywordBackground, SqlAssistMonikers.BlockKeywordBackground, s => s.BlockKeywordBackground, ThemeBrush.BlockKeywordBackground);
        AddColorCommand(CommandIds.PickBlockSymbolForeground, SqlAssistMonikers.BlockSymbolForeground, s => s.BlockSymbolForeground, ThemeBrush.BlockSymbolForeground);
        AddColorCommand(CommandIds.PickBlockSymbolBackground, SqlAssistMonikers.BlockSymbolBackground, s => s.BlockSymbolBackground, ThemeBrush.BlockSymbolBackground);

        // 結果格線的右鍵選單。狀態由 ResultGridActions 回答：找不到格線就停用，
        // 但仍然看得見——使用者因此知道這個功能存在，只是現在沒有東西可以做。

        AddCommand(
            CommandIds.ResultGridTempTable,
            (_, _) => ResultGridActions.CreateTempTableScript(_package),
            ResultGridActions.IsAvailable);

        AddCommand(
            CommandIds.ResultGridInPredicate,
            (_, _) => ResultGridActions.CopyInPredicate(_package),
            ResultGridActions.IsAvailable);

        AddCommand(
            CommandIds.ResultGridJson,
            (_, _) => ResultGridActions.CopyJson(_package),
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
        [Localizable(false)] string displayName)
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

        Add(menuCommand);
    }

    /// <summary>
    /// 註冊命令，並在每次查詢狀態時依目前語言設定選單文字。
    /// </summary>
    /// <remarks>
    /// 命令表每一顆都標了 <c>TextChanges</c>，殼層才會照 QueryStatus 回報的 <c>Text</c> 換字；
    /// 每次都設而不是換語言時才設：殼層在開選單當下才問，沒有「現在去改已經畫好的選單」這種入口。
    /// 命令表自己的 <c>ButtonText</c> 只在套件載入前出現。
    /// </remarks>
    private void Add(OleMenuCommand menuCommand)
    {
        var id = menuCommand.CommandID.ID;
        menuCommand.BeforeQueryStatus += (_, _) =>
        {
            if (MenuLabel(id) is { } text) menuCommand.Text = text;
        };
        _commandService.AddCommand(menuCommand);
    }

    /// <summary>命令表每一顆按鈕的顯示文字；<c>tools/Test-CommandTable.ps1</c> 核對兩邊一一對應。</summary>
    private static string? MenuLabel(int commandId) => commandId switch
    {
        CommandIds.ShowSqlHistory => MenuText.ShowSqlHistory,
        CommandIds.ShowSqlFavorites => MenuText.ShowSqlFavorites,
        CommandIds.ShowSqlMemoryUsage => MenuText.ShowSqlMemoryUsage,
        CommandIds.ShowSqlSearch => MenuText.ShowSqlSearch,
        CommandIds.AddToFavorites or CommandIds.AddToFavoritesFromTools => MenuText.AddToFavorites,
        CommandIds.ToggleEnabled => MenuText.ToggleEnabled,
        CommandIds.ToggleSuggestions => MenuText.ToggleSuggestions,
        CommandIds.GoToDefinition => MenuText.GoToDefinition,
        CommandIds.ShowObjectStructure => MenuText.ShowObjectStructure,
        CommandIds.RefreshSuggestions => MenuText.RefreshSuggestions,
        CommandIds.SurroundWith or CommandIds.SurroundWithFromTools => MenuText.SurroundWith,
        CommandIds.ManageSnippets => MenuText.ManageSnippets,
        CommandIds.OpenSettings => MenuText.OpenSettings,
        CommandIds.FocusNotifications => MenuText.FocusNotifications,
        CommandIds.CheckForUpdates => MenuText.CheckForUpdates,
        CommandIds.ShowDiagnostics => MenuText.ShowDiagnostics,
        CommandIds.OpenDiagnosticsLog => MenuText.OpenDiagnosticsLog,
        CommandIds.ShowSqlMemoryUsageFromSettings => MenuText.ShowSqlMemoryUsageFromSettings,
        CommandIds.PickBlockAccent => MenuText.PickBlockAccent,
        CommandIds.PickBlockKeywordForeground => MenuText.PickBlockKeywordForeground,
        CommandIds.PickBlockKeywordBackground => MenuText.PickBlockKeywordBackground,
        CommandIds.PickBlockSymbolForeground => MenuText.PickBlockSymbolForeground,
        CommandIds.PickBlockSymbolBackground => MenuText.PickBlockSymbolBackground,
        CommandIds.ResultGridInPredicate => MenuText.ResultGridInPredicate,
        CommandIds.ResultGridMarkdown => MenuText.ResultGridMarkdown,
        CommandIds.ResultGridJson => MenuText.ResultGridJson,
        CommandIds.ResultGridTempTable => MenuText.ResultGridTempTable,
        CommandIds.ResultGridProfile => MenuText.ResultGridProfile,
        CommandIds.ResultGridCell => MenuText.ResultGridCell,
        CommandIds.ProbeResultGrid => MenuText.ProbeResultGrid,
        _ => null,
    };

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

        Add(menuCommand);
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
                SqlAssistStatusBar.Show(_package, CommandText.Disabled);
                return;
            }

            if (ActiveSqlEditor.Current is not { } textView)
            {
                SqlAssistStatusBar.Show(_package, CommandText.PlaceCaretInQueryWindow);
                return;
            }

            SqlObjectNavigation.GoToDefinition(textView, textView.Caret.Position.BufferPosition, _package);
        }
        catch (Exception exception)
        {
            // 同上：這條路徑綁著按鍵，例外也走狀態列。
            SqlAssistDiagnostics.WriteAlways($"開啟物件定義失敗：{exception}");
            SqlAssistStatusBar.Show(_package, EditorText.DefinitionFailed);
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
        SqlAssistDiagnostics.WriteAlways("顯示物件結構命令抵達 SqlAssist（命令表）");

        // BeforeQueryStatus 通常會擋掉，但殼層不保證每一次派送前都問過狀態。
        if (!SqlAssistSettingsStore.Current.Enabled)
        {
            SqlAssistStatusBar.Show(_package, CommandText.Disabled);
            return;
        }

        if (ActiveSqlEditor.Current is not { } textView)
        {
            SqlAssistStatusBar.Show(_package, CommandText.PlaceCaretInQueryWindow);
            return;
        }

        SqlObjectNavigation.ShowStructure(textView, textView.Caret.Position.BufferPosition, _package);
    }

    /// <summary>
    /// 以片段包住選取範圍。
    /// </summary>
    /// <remarks>
    /// 與 <c>Edit.SurroundWith</c> 走同一份實作（<see cref="SqlSnippetSurroundAction"/>）。
    /// 回饋一律走狀態列，<b>不用對話框</b>——這個命令也可能從濾鏡那條按鍵路徑進來，
    /// 而一個要按確定才消失的視窗出現在按鍵路徑上，比沒有反應更糟。
    /// </remarks>
    private void SurroundWith(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            SqlAssistDiagnostics.Write("包夾命令抵達 SqlAssist（命令表）");

            // BeforeQueryStatus 已經擋掉這幾種，但殼層不保證每一次派送前都問過狀態。
            if (!SqlAssistSettingsStore.Current.Enabled)
            {
                SqlAssistStatusBar.Show(_package, CommandText.Disabled);
                return;
            }

            if (ActiveSqlEditor.Current is not { } textView)
            {
                SqlAssistStatusBar.Show(_package, CommandText.PlaceCaretInQueryWindow);
                return;
            }

            if (!SqlSnippetSurroundAction.TryBegin(textView, out var message))
            {
                SqlAssistStatusBar.Show(_package, message);
            }
        }
        catch (Exception exception)
        {
            // 同上：這條路徑綁著按鍵，例外也走狀態列。
            SqlAssistDiagnostics.WriteAlways($"以片段包住選取範圍失敗：{exception}");
            SqlAssistStatusBar.Show(_package, CommandText.SurroundFailed);
        }
    }

    /// <summary>
    /// 把查詢視窗目前的 SQL 加進 SQL Memory 的收藏。
    /// </summary>
    /// <remarks>
    /// 回饋走狀態列而不是對話框：成功與取消都已經由收藏對話框自己交代完，
    /// 再彈一個要按確定的視窗只是多一次點擊。失敗的原因仍然看得見。
    /// </remarks>
    private void AddToFavorites(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            // BeforeQueryStatus 通常會擋掉，但殼層不保證每一次派送前都問過狀態。
            if (!SqlAssistSettingsStore.Current.Enabled)
            {
                SqlAssistStatusBar.Show(_package, CommandText.Disabled);
                return;
            }

            if (!SqlMemoryHost.Runtime.IsAvailable)
            {
                SqlAssistStatusBar.Show(_package, CommandText.SqlMemoryDisabledForFavorite);
                return;
            }

            if (ActiveSqlEditor.Current is not { } textView)
            {
                SqlAssistStatusBar.Show(_package, CommandText.PlaceCaretInQueryWindow);
                return;
            }

            if (SqlMemoryFavoriteAction.Begin(textView, _package) is { Length: > 0 } message)
            {
                SqlAssistStatusBar.Show(_package, message);
            }
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"新增至收藏失敗：{exception}");
            SqlAssistStatusBar.Show(_package, CommandText.AddFavoriteFailed);
        }
    }

    /// <summary>「聚焦通知」：沒有東西可以聚焦時在狀態列說一聲，不是按了沒反應。</summary>
    private void FocusNotifications(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!NotificationIslandController.Default.Focus())
            SqlAssistStatusBar.Show(_package, CommandText.NoNotifications);
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
        ShowMessage(CommandText.OperationFailed(operation, exception.Message), OLEMSGICON.OLEMSGICON_CRITICAL);
    }

    private void ShowMessage(string message, OLEMSGICON icon = OLEMSGICON.OLEMSGICON_WARNING)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VsShellUtilities.ShowMessageBox(
            _package,
            message,
            "SqlAssist",
            icon,
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
    /// 成功只回報狀態列，完整報告留在紀錄檔；失敗仍以原生訊息框說明原因。
    /// </remarks>
    private void ProbeResultGrid(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var report = SqlAssistResultGridProbe.Run();
            var summary = Array.Find(
                report.Split('\n'),
                line => line.StartsWith(SqlAssistResultGridProbe.GridCountLabel, StringComparison.Ordinal))
                ?.Trim() ?? SqlAssistResultGridProbe.MissingGridCountLine;

            SqlAssistStatusBar.Show(_package, CommandText.ProbeCompleted(summary));
        }
        catch (Exception exception)
        {
            Report(CommandText.OperationProbeResultGrid, exception);
        }
    }

    private void ShowAboutAndDiagnostics(object? sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var snapshot = SqlAssistDiagnosticSnapshotFactory.Create();
            new SqlAssistAboutWindow(snapshot, TryOpenSettings, OpenDiagnosticsLogCore,
                () => SqlAssistUpdateCheckCommand.Execute(_package)).ShowModal();
        }
        catch (Exception exception)
        {
            Report(CommandText.OperationOpenAbout, exception);
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
            Report(CommandText.OperationOpenSnippetManager, exception);
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

            ShowMessage(CommandText.SettingsUnavailable);
        }
        catch (Exception exception)
        {
            Report(CommandText.OperationOpenSettings, exception);
        }
    }

    private bool TryOpenSettings() => TryOpenSettings(_package);

    internal static bool TryOpenSettings(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

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
            Report(CommandText.OpenDiagnosticsLog, exception);
        }
    }

    private void AddColorCommand(int commandId, string moniker, Func<SqlAssistSettings, string> read, ThemeBrush role)
    {
        AddCommand(commandId, (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // 使用者選色與設定寫入失敗要顯示原因，不交給 Guard 靜默降級。
            try
            {
                IServiceProvider services = _package;
                if (services.GetService(typeof(SVsUIShell)) is not IVsUIShell shell)
                {
                    ShowMessage(CommandText.ColorPickerUnavailable);
                    return;
                }
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(shell.GetDialogOwnerHwnd(out var owner));
                var color = SqlColorPicker.Pick(owner, read(SqlAssistSettingsStore.Current), ((SolidColorBrush)VsThemeBrushes.Get(role)).Color);
                if (color is not null && !SqlAssistSettingsStore.TrySetValue(moniker, color))
                    ShowMessage(CommandText.ColorNotSaved);
            }
            catch (Exception exception)
            {
                Report(CommandText.OperationPickColor, exception);
            }
        });
    }

    private void OpenDiagnosticsLogCore()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SqlAssistDiagnostics.EnsureLogFile();
        VsShellUtilities.OpenDocument(_package, SqlAssistDiagnostics.LogPath);
    }

    /// <remarks>
    /// 不必另外去戳已經開著的清單：原生管線每次觸發都會重新問來源。
    /// 清的也不只是快取內容——連「現在連到哪個資料庫」都要重新確認，否則換過
    /// 資料庫之後按下它，重新載入的仍然是舊資料庫的物件，而那正是使用者按它的
    /// 原因；兩件事一起由 <see cref="SqlMetadataService.InvalidateAll"/> 做掉。
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
                CommandText.SuggestionsRefreshed);
        }
        catch (Exception exception)
        {
            // 這條路徑綁著按鍵，失敗必須可見，但不應用對話框打斷編輯。
            SqlAssistDiagnostics.WriteAlways($"重新整理建議失敗：{exception}");
            SqlAssistStatusBar.Show(_package, CommandText.RefreshSuggestionsFailed);
        }
    }
}
