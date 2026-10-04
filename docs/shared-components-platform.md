# 平台共用元件

本頁只列 Ssms22 接線層（編輯器、殼層、連線）與工具腳本的唯一出處；自製 UI 元件見[UI 共用元件](shared-components-ui.md)，
純邏輯見[共用元件表](shared-components.md)，SQL Memory 專屬元件見[專屬表](shared-components-sql-memory.md)。

| 這件事 | 唯一出處 |
|---|---|
| 讀取 SSMS 結果格線與換算兩套欄索引 | `Ssms22/ResultGrid/SsmsResultGrid.cs` |
| DPI、螢幕工作區、Popup 搬位 | `Ssms22/Preview/NativeScreen.cs` |
| 背景結果寫回 SQL 編輯器 | `Ssms22/Editor/TextViewEditCoordinator.cs`、`ActiveSqlEditor.cs` |
| F12、預覽的指令碼選項 | `Ssms22/Settings/SqlScriptPreferences.cs` |
| 某個位置的物件開預覽或定義 | `Ssms22/Editor/SqlObjectNavigation.cs` |
| 物件總管的伺服器、連線與導航 | `Ssms22/Connections/SsmsObjectExplorer.cs` |
| SSMS 狀態列的進度與失敗 | `Ssms22/SqlAssistStatusBar.cs` |
| 主視窗、作用中框架、元素所在視窗（取代 `Window.GetWindow`）、焦點、非啟用浮窗帶回前景與對話框擁有者 | `Ssms22/UI/SsmsWindows.cs` |
| 編輯器換行判定 | `Ssms22/Editor/SnapshotNewLine.cs` |
| 延後至本輪命令結束 | `Ssms22/Editor/TextViewDispatch.cs` |
| 跟著游標出現與收起的提示 | `Ssms22/Editor/CaretHint.cs` |
| Tab／Shift+Tab／Enter 優先順序 | `Ssms22/Editor/SqlTabCommandHandler.cs` |
| 殼層命令攔截與診斷 | `Ssms22/Editor/SqlShellCommandFilter.cs` |
| 自製 Popup 握著鍵盤時把殼層命令換回按鍵交還 | `Ssms22/Editor/ShellKeyCapture.cs`、`ShellKeyMap.cs` |
| ALTER／INSERT／MERGE／EXEC／函式引數的提交後改寫 | `Ssms22/Completion/SqlCommitExpander.cs` |
| 平台邊界例外處理 | `Ssms22/SqlAssistPlatformGuard.cs` |
| 重開建議清單 | `Ssms22/Completion/SqlCompletionReopen.cs` |
| SQL 語言服務 GUID | `Ssms22/SqlLanguageService.cs` |
| 抑制 SSMS 內建自動建議清單 | `Ssms22/Settings/NativeMemberList.cs` |
| 查詢視窗的伺服器與資料庫、連線字串裡的伺服器名稱 | `Ssms22/Connections/SqlWindowConnections.cs` |
| UTF-8 輸出、SSMS 路徑與擴充 Id 探索 | `tools/SqlAssist.Tools.psm1` |
| 部署預檢、SHA-256 與 VSIX 白名單 | `tools/SqlAssist.Deployment.psm1` |
| 診斷紀錄的排隊、批次寫檔與倒出 | `Ssms22/SqlAssistDiagnostics.cs` |
