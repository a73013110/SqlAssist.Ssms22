# 平台共用元件

本頁只列 Ssms22 接線層與工具腳本的唯一出處；純邏輯見[共用元件表](shared-components.md)，
SQL Memory 專屬元件見[專屬表](shared-components-sql-memory.md)。同一路徑的相關能力合併列出，
需要符號時再於該檔搜尋。

| 這件事 | 唯一出處 |
|---|---|
| 讀取 SSMS 結果格線與換算兩套欄索引 | `Ssms22/ResultGrid/SsmsResultGrid.cs` |
| DPI、螢幕工作區 | `Ssms22/Preview/NativeScreen.cs` |
| 背景結果寫回既有或新建的 SQL 編輯器 | `Ssms22/Editor/TextViewEditCoordinator.cs`、`ActiveSqlEditor.cs` |
| F12、預覽的指令碼選項 | `Ssms22/Settings/SqlScriptPreferences.cs` |
| 物件總管的伺服器、連線與導航 | `Ssms22/Connections/SsmsObjectExplorer.cs` |
| SSMS 狀態列的進度與失敗 | `Ssms22/SqlAssistStatusBar.cs` |
| 編輯器換行判定 | `Ssms22/Editor/SnapshotNewLine.cs` |
| 延後至本輪命令結束 | `Ssms22/Editor/TextViewDispatch.cs` |
| Tab／Shift+Tab／Enter 優先順序 | `Ssms22/Editor/SqlTabCommandHandler.cs` |
| 殼層命令攔截與診斷 | `Ssms22/Editor/SqlShellCommandFilter.cs` |
| ALTER／INSERT／MERGE／EXEC／函式引數的提交後改寫 | `Ssms22/Completion/SqlCommitExpander.cs` |
| 平台邊界例外處理 | `Ssms22/SqlAssistPlatformGuard.cs` |
| 重開建議清單 | `Ssms22/Completion/SqlCompletionReopen.cs` |
| SQL 語言服務 GUID | `Ssms22/SqlLanguageService.cs` |
| 抑制 SSMS 內建自動建議清單 | `Ssms22/Settings/NativeMemberList.cs` |
| 停駐工具窗主從區 | `Ssms22/UI/MasterDetailView.cs` |
| 字型、基本控制項、水平資訊列、分頁、表面淡入與動作色調 | `Ssms22/UI/SqlAssistChrome.cs` |
| 清單列、標頭骨架、右緣操作層與窄版降級 | `Ssms22/UI/SqlAssistChrome.Rows.cs` |
| 清單列寬度模式與門檻 | `Ssms22/UI/SqlRowLayout.cs` |
| 過濾面板與摘要 | `Ssms22/UI/SqlFilterFlyout.cs`、`SqlFilterSummary.cs`、`SqlAssistChrome.Filters.cs` |
| 可換行的工具列篩選 | `Ssms22/UI/SqlFilterBar.cs` |
| 輸入框與右緣動作的第一列 | `Ssms22/UI/SqlInputRow.cs` |
| 搜尋框與工具列的開關樣式 | `Ssms22/UI/SqlAssistChrome.Search.cs` |
| Chevron、圖示按鈕／開關與兩級分隔線 | `Ssms22/UI/SqlAssistChrome.Buttons.cs` |
| 卡片樣式與進退場 | `Ssms22/UI/SqlAssistChrome.Cards.cs` |
| 卡片清單的鍵盤、滑鼠、續頁與頁尾 | `Ssms22/UI/SqlCardList.cs` |
| 已選條件 chip 列 | `Ssms22/UI/SqlFilterChipBar.cs` |
| 選取驅動的去彈跳、取消與 stale guard | `Ssms22/UI/SqlSelectionLoader.cs` |
| 載入、空、失敗、權限不足與行內忙碌狀態 | `Ssms22/UI/SqlStateSurface.cs`、`SqlSurfaceState.cs`、`SqlBusyNotice.cs` |
| 搜尋、預覽與估算的去彈跳長度 | `Ssms22/UI/SqlAssistChrome.Delays.cs` |
| 對話框元件與殼層 | `Ssms22/UI/SqlAssistChrome.Dialogs.cs`、`SqlAssistDialogs.cs` |
| SQL 唯讀／著色編輯、分類、選取映射與主題 | `Ssms22/UI/SqlReadOnlyViewer.cs`、`SqlTextEditor.cs`、`Ssms22/Preview/SqlScriptDocument.cs`、`SqlScriptTheme.cs` |
| 命中與區塊端點配色 | `Ssms22/UI/TextMarkColors.cs`、`MatchPalette.cs`；大面積分類色見[文字標記](text-marks.md) |
| 上一處／下一處命中與讀數 | `Ssms22/UI/SqlMatchNavigator.cs`、`Core/Matching/MatchCursor.cs` |
| WPF 資料格匯出、顯示順序與空欄 | `Ssms22/UI/SqlDataGridText.cs` |
| SQL 原生圖示、語意圖示與影像插槽 | `Ssms22/UI/SqlIcons.cs`、`SqlIcon.cs`、`SqlIcons.Images.cs`、`SqlIconImage.cs` |
| 宿主筆刷、主題色階、動作對比與動態資源刷新 | `Ssms22/UI/VsThemeBrushes.cs`、`ThemePalette.cs`、`ThemeColorMath.cs`、`ThemeResourceSet.cs`、`ThemeRefreshQueue.cs` |
| 通知內容、單一卡片、生命週期與視窗宿主 | `Ssms22/Notifications/NotificationPresenter.cs`、`NotificationSurface.cs`、`NotificationSurfaceController.cs`、`NotificationWindowHost.cs` |
| UTF-8 輸出、SSMS 路徑與擴充 Id 探索 | `tools/SqlAssist.Tools.psm1` |
| 部署預檢、SHA-256 與 VSIX 白名單 | `tools/SqlAssist.Deployment.psm1` |
| 診斷紀錄的排隊、批次寫檔與倒出 | `Ssms22/SqlAssistDiagnostics.cs` |
