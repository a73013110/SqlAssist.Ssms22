# UI 共用元件

本頁只列 Ssms22 自製 UI（工具窗、清單、篩選、浮層、通知島）的唯一出處；編輯器與殼層接線見
[平台共用元件](shared-components-platform.md)，外觀準則見[自製 UI 準則](ui-guidelines.md)。

| 這件事 | 唯一出處 |
|---|---|
| 停駐工具窗基底：開啟（輸入後才顯示、焦點進窗）、滑出後補排殼層根 | `Ssms22/UI/SqlToolWindowPane.cs` |
| 停駐工具窗主從區 | `Ssms22/UI/MasterDetailView.cs` |
| 字型、基本控制項、資訊列、分頁（含右側工具 `TabStripTrailing`）、淡入與動作色調 | `Ssms22/UI/SqlAssistChrome.cs` |
| 膠囊與分頁標籤（分頁一律由 `SqlAssistChrome.CreateTab` 建） | `Ssms22/UI/SqlPill.cs`、`SqlTabHeader.cs` |
| 清單列、標頭骨架、右緣操作層、窄版降級與寬度門檻 | `Ssms22/UI/SqlAssistChrome.Rows.cs`、`SqlRowLayout.cs` |
| 過濾面板、摘要與可換行的工具列篩選 | `Ssms22/UI/SqlFilterFlyout.cs`、`SqlFilterSummary.cs`、`SqlAssistChrome.Filters.cs`、`SqlFilterBar.cs` |
| 輸入框與右緣動作的第一列 | `Ssms22/UI/SqlInputRow.cs` |
| 搜尋框、工具列開關樣式與比對開關（寫回 `TextMatchOptions` 不發變更） | `Ssms22/UI/SqlAssistChrome.Search.cs`、`SqlMatchToggles.cs` |
| 套用查詢視窗連線的按鈕與用詞 | `Ssms22/UI/SqlAssistChrome.Buttons.cs` 的 `CreateEditorConnectionButton`、`SqlEditorConnectionText.cs` |
| Chevron、圖示按鈕／開關、兩級分隔線、工具列；卡片樣式與進退場 | `Ssms22/UI/SqlAssistChrome.Buttons.cs`、`SqlAssistChrome.Cards.cs` |
| 卡片清單的鍵盤、滑鼠、續頁與頁尾 | `Ssms22/UI/SqlCardList.cs`、頁尾 `SqlListPager.cs` |
| 清單多選：以 Id 為鍵的勾選、錨點、全部符合、動作派送、勾選欄與外觀 | `Ssms22/UI/SqlCardSelection.cs`（清單端 `SqlCardListBase.EnableSelection`）、`SqlRowCheck.cs`、`SqlAssistChrome.Selection.cs` |
| 蓋在輸入列上的選取工具列（筆數、全選、動作、進度與取消） | `Ssms22/UI/SqlSelectionBar.cs` |
| 剪貼簿寫入純文字或 TSV＋HTML、鎖住時重試與回報字 | `Ssms22/UI/SqlClipboard.cs` |
| 選取驅動的去彈跳、取消與 stale guard | `Ssms22/UI/SqlSelectionLoader.cs` |
| 載入、空、失敗、權限不足、不完整與行內忙碌狀態 | `Ssms22/UI/SqlStateSurface.cs`、`SqlSurfaceState.cs`、`SqlBusyNotice.cs` |
| 清單上方的進度條、說明與停止 | `Ssms22/UI/SqlProgressStrip.cs`（進度條沿用 `SqlUsageMeter`） |
| 搜尋、預覽與估算的去彈跳長度 | `Ssms22/UI/SqlAssistChrome.Delays.cs` |
| 對話框元件與殼層 | `Ssms22/UI/SqlAssistChrome.Dialogs.cs`、`SqlAssistDialogs.cs` |
| SQL 唯讀／著色編輯、分類、選取映射與主題 | `Ssms22/UI/SqlReadOnlyViewer.cs`、`SqlTextEditor.cs`、`Ssms22/Preview/SqlScriptDocument.cs`、`SqlScriptTheme.cs` |
| 命中與區塊端點配色 | `Ssms22/UI/TextMarkColors.cs`、`MatchPalette.cs`；大面積分類色見[文字標記](text-marks.md) |
| 上一處／下一處命中、讀數，預覽上標命中並停在第一處 | `Ssms22/UI/SqlMatchNavigator.cs`、`SqlMatchNavigation.cs` |
| WPF 資料格匯出、顯示順序、空欄與列篩選 | `Ssms22/UI/SqlDataGridText.cs`（加引號規則在 `SqlTabularText`） |
| 一行文字上的命中高亮 | `Ssms22/UI/SqlHighlightText.cs` |
| SQL 原生圖示、語意圖示與影像插槽 | `Ssms22/UI/SqlIcons.cs`、`SqlIcon.cs`、`SqlIcons.Images.cs`、`SqlIconImage.cs` |
| 宿主筆刷、主題色階、動作對比與動態資源刷新 | `Ssms22/UI/VsThemeBrushes.cs`、`ThemePalette.cs`、`TextSelectionColors.cs`、`ThemeColorMath.cs`、`ThemeResourceSet.cs`、`ThemeRefreshQueue.cs` |
| 通知島的形態狀態機、浮層定位、表面、提醒檢視、附條、對齊基準、狀態圖示與換字 | `Ssms22/Notifications/NotificationIslandState.cs`、`NotificationPlacement.cs`、`Ssms22/UI/NotificationIsland.cs`、`NotificationPromptView.cs`、`NotificationActivityStrip.cs`、`NotificationLayout.cs`、`NotificationTicker.cs` |
| 通知專屬的動畫節奏；可中斷的彈簧 | `Ssms22/UI/NotificationMotion.cs`、`SpringMotion.cs` |
| 浮層（通知島、浮動預覽）的進出場、進度圈、膠囊與狀態圖示 | `Ssms22/UI/SurfaceMotion.cs`、`SurfaceCapsule.cs`、`SurfaceStatusIcon.cs` |
| 結構預覽的內容與分層載入（浮動與工具窗共用）；浮動外殼 | `Ssms22/Preview/SqlStructurePanel.cs`、`SqlStructurePresenter.cs`；`PreviewSurface.cs` |
| 浮層的圓角、柔影與叉號 | `Ssms22/UI/SqlAssistChrome.Surfaces.cs` |
| 通知內容、活動期限、全域控制器、右下浮層與提醒按鈕派送 | `Ssms22/Notifications/NotificationPresenter.cs`、`NotificationLifecycle.cs`、`NotificationIslandController.cs`、`NotificationOverlay.cs`、`NotificationActionRouter.cs` |

新清單接上多選照 SQL Memory 與 SQL Search 的做法：列實作 `ISqlCheckableRow`，樣板以
`WrapWithRowCheck` 包起來並呼叫 `RevealRowCheck`，卡片樣式開 `checkable`，宿主建一份
`SqlCardSelection` 交給清單與 `SqlSelectionBar`，再加自己的 `SqlSelectionAction`。
