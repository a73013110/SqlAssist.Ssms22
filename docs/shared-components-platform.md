# 平台共用元件

範圍：Ssms22 接線層與工具腳本的唯一出處。純邏輯見[共用元件表](shared-components.md)。

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
| 字型、按鈕、輸入欄位、資料格樣板 | `Ssms22/UI/SqlAssistChrome.cs` |
| WPF 資料格的選取匯出、顯示順序與空欄讀值 | `Ssms22/UI/SqlDataGridText.cs` |
| SQL 圖示（補全、結構預覽與 QuickInfo 的原生圖示及快取） | `Ssms22/UI/SqlIcons.cs` |
| 佈景主題筆刷 | `Ssms22/UI/VsThemeBrushes.cs` |
| 腳本的 UTF-8 輸出、SSMS 路徑與擴充 Id 探索 | `tools/SqlAssist.Tools.psm1` |
| 主題色階推導與雙表面對比 | `Ssms22/UI/ThemePalette.cs`、`ThemeColorMath.cs` |
| 動態配色資源與合併更新通知 | `Ssms22/UI/ThemeResourceSet.cs`、`ThemeRefreshQueue.cs` |
