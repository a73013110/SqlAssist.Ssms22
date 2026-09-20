# 平台共用元件

範圍：Ssms22 接線層與工具腳本的唯一出處。純邏輯見[共用元件表](shared-components.md)，
SQL Memory 見[專屬表](shared-components-sql-memory.md)。

| 這件事 | 唯一出處 |
| --- | --- |
| 從 SSMS 結果格線取資料（兩套欄索引只換算一次） | `Ssms22/ResultGrid/SsmsResultGrid.cs` |
| DPI 與螢幕工作區換算 | `Ssms22/Preview/NativeScreen.cs` |
| 背景結果寫回編輯器（替換既有文字與寫進空白緩衝區） | `Ssms22/Editor/TextViewEditCoordinator.cs` |
| 目前的 SQL 編輯器，以及取回剛建立的那一個 | `Ssms22/Editor/ActiveSqlEditor.cs` |
| F12 與預覽要用哪一組指令碼選項 | `Ssms22/Settings/SqlScriptPreferences.cs` |
| 進度與失敗顯示在 SSMS 狀態列 | `Ssms22/SqlAssistStatusBar.cs` |
| 寫回去的多行文字用哪一種換行 | `Ssms22/Editor/SnapshotNewLine.cs` |
| 排到「這一輪命令結束之後」再做 | `Ssms22/Editor/TextViewDispatch.cs` |
| Tab／Shift+Tab／Enter 的優先順序 | `Ssms22/Editor/SqlTabCommandHandler.cs` |
| 攔截殼層命令（F12…），以及「按了沒反應」時的命令診斷 | `Ssms22/Editor/SqlShellCommandFilter.cs` |
| 提交後改寫文字（ALTER／INSERT／MERGE／EXEC／函式引數五種共用） | `Ssms22/Completion/SqlCommitExpander.cs` |
| 平台邊界的例外處理 | `Ssms22/SqlAssistPlatformGuard.cs` |
| 重開建議清單的三個步驟 | `Ssms22/Completion/SqlCompletionReopen.cs` |
| SQL 語言服務 GUID | `Ssms22/SqlLanguageService.cs` |
| 擋掉 SSMS 內建的自動建議清單 | `Ssms22/Settings/NativeMemberList.cs` |
| 字型、按鈕、輸入欄位、資料格樣板、覆蓋式捲軸 | `Ssms22/UI/SqlAssistChrome.cs` |
| 圖示加標籤的分頁（SQL Memory、SQL Search 與之後的工具窗） | `Ssms22/UI/SqlAssistChrome.cs` 的 `CreateIconTab` |
| 過濾彈出面板的選項清單（虛擬化、標題與選項兩種列） | `Ssms22/UI/SqlAssistChrome.Search.cs` 的 `CreateSearchOptionList` |
| 內容表面出現時的淡入（浮動預覽、SQL Memory 復原卡片） | `Ssms22/UI/SqlAssistChrome.cs` 的 `PlayAppear` |
| 對話框的資訊列、分段、分段卡片、選項列、頁尾與破壞性主要動作 | `Ssms22/UI/SqlAssistChrome.Dialogs.cs` |
| 對話框殼層（標題、尺寸、主題、字型、置中） | `Ssms22/UI/SqlAssistDialogs.cs` |
| 獨立 SQL 唯讀預覽／著色編輯 | `Ssms22/UI/SqlReadOnlyViewer.cs`／`SqlTextEditor.cs`（外觀由呼叫端掛 `SqlScriptTheme`） |
| SQL 著色分類、原文選取映射與編輯器主題適配 | `Ssms22/Preview/SqlScriptDocument.cs`（`Classify`）／`SqlScriptTheme.cs` |
| WPF 資料格的選取匯出、顯示順序與空欄讀值 | `Ssms22/UI/SqlDataGridText.cs` |
| SQL 圖示（補全、結構預覽與 QuickInfo 的原生圖示及快取） | `Ssms22/UI/SqlIcons.cs` |
| 自製 UI 的語意圖示與 moniker 對照、原生影像插槽 | `Ssms22/UI/SqlIcon.cs`、`SqlIcons.Images.cs`、`SqlIconImage.cs` |
| 佈景主題筆刷 | `Ssms22/UI/VsThemeBrushes.cs` |
| 腳本的 UTF-8 輸出、SSMS 路徑與擴充 Id 探索 | `tools/SqlAssist.Tools.psm1` |
| Debug 部署預檢、SHA-256 與 VSIX 必要檔案白名單 | `tools/SqlAssist.Deployment.psm1` |
| 主題色階推導與雙表面對比 | `Ssms22/UI/ThemePalette.cs`、`ThemeColorMath.cs` |
| 動作的語意色調（停駐／按下的底色與配對前景） | `Ssms22/UI/SqlAssistChrome.cs` 的 `SqlActionTone`、`ThemePalette.cs` |
| 動態配色資源與合併更新通知 | `Ssms22/UI/ThemeResourceSet.cs`、`ThemeRefreshQueue.cs` |
| 通知該顯示什麼（可見度、合併、措辭、關閉與展開狀態） | `Ssms22/Notifications/NotificationPresenter.cs` |
| 通知卡片本身（整個處理程序一張，在宿主之間搬家） | `Ssms22/Notifications/NotificationSurface.cs` |
| 通知何時顯示、掛在哪個宿主（唯一計時器與訂閱） | `Ssms22/Notifications/NotificationSurfaceController.cs` |
| 讓 SqlAssist 的 WPF 視窗接通知卡片 | `Ssms22/Notifications/NotificationWindowHost.cs`（對話框經 `SqlAssistDialogs.Configure` 自動註冊） |
| 診斷紀錄的排隊、批次寫檔與倒出 | `Ssms22/SqlAssistDiagnostics.cs` |
