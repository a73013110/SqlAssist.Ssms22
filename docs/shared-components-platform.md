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
| 停駐工具窗的主從區（轉向門檻、收合把手與兩個方向的比例） | `Ssms22/UI/MasterDetailView.cs` |
| 字型、按鈕、輸入欄位、資料格樣板、覆蓋式捲軸 | `Ssms22/UI/SqlAssistChrome.cs` |
| 清單列的共用元件（膠囊、身分組、名稱上限、列操作與 overflow、圖示標籤） | `Ssms22/UI/SqlAssistChrome.Rows.cs` |
| 清單列第一列的骨架（疊層、宣告順序、操作層、窄版降級） | `Ssms22/UI/SqlAssistChrome.Rows.cs` 的 `BeginRowHeading`／`SqlRowHeading` |
| 列操作層與它的揭露（浮在右緣不佔寬度、滑鼠與鍵盤同一條路） | `Ssms22/UI/SqlAssistChrome.Rows.cs` 的 `CreateRowActionLayer`／`RevealRowActions` |
| 單列、可水平捲動的資訊列（Preview 摘要、已選條件列） | `Ssms22/UI/SqlAssistChrome.cs` 的 `CreateHorizontalStrip` |
| Shift＋滾輪、樹裡第一個 `ScrollViewer` | `Ssms22/UI/SqlAssistChrome.cs` 的 `ApplyShiftWheelPan`／`FindScrollViewer` |
| 清單列的寬度模式與窄版門檻（宿主量一次，可繼承） | `Ssms22/UI/SqlRowLayout.cs` |
| 圖示加標籤的分頁 | `Ssms22/UI/SqlAssistChrome.cs` 的 `CreateIconTab` |
| 過濾面板（單／複選、第一列的預設、續頁、排序、整批命令） | `Ssms22/UI/SqlFilterFlyout.cs` |
| 過濾按鈕上那一句摘要（沒勾／勾一個／勾很多）與 Tooltip 的完整名單 | `Ssms22/UI/SqlFilterSummary.cs` |
| 面板裡那條橫線、過濾面板的按鈕樣式、第一列那個預設與選項清單（虛擬化、兩種列） | `Ssms22/UI/SqlAssistChrome.Filters.cs` |
| 工具列那一列篩選（分群、換行） | `Ssms22/UI/SqlFilterBar.cs` |
| 工具列第一列：輸入框吃剩餘寬度、右緣圖示鈕 | `Ssms22/UI/SqlInputRow.cs` |
| 開關「開著」的外觀（搜尋框裡那兩顆與工具列上的圖示開關共用） | `Ssms22/UI/SqlAssistChrome.Search.cs` 的 `CreateToggleStyle` |
| 展開／收合箭頭與它的轉向（下拉、排序選單、預覽把手） | `Ssms22/UI/SqlAssistChrome.Buttons.cs` 的 `CreateChevron`／`SetChevronExpanded` |
| 工具列與預覽的圖示鈕（一次動作）與圖示開關（維持著的狀態，如顯示換行） | `Ssms22/UI/SqlAssistChrome.Buttons.cs` 的 `CreateIconButton`／`CreateIconToggle` |
| 工具列上兩級的分隔線（篩選列的分群、預覽工具列的導覽與命令） | `Ssms22/UI/SqlAssistChrome.Buttons.cs` 的 `CreateGroupDivider`／`CreateItemDivider` |
| 清單列的卡片容器樣式與進場／退場 | `Ssms22/UI/SqlAssistChrome.Cards.cs` |
| 卡片清單的鍵盤、滑鼠、續頁與頁尾容器 | `Ssms22/UI/SqlCardList.cs` 的 `SqlCardListBase<TAction>` |
| 已選條件的 chip 列（一維度一顆、橫向捲動） | `Ssms22/UI/SqlFilterChipBar.cs` |
| 內容表面出現時的淡入（浮動預覽、SQL Memory 復原卡片） | `Ssms22/UI/SqlAssistChrome.cs` 的 `PlayAppear` |
| 選取驅動的讀取（去彈跳、取消上一輪、回來時的 stale guard） | `Ssms22/UI/SqlSelectionLoader.cs` |
| 載入、空、讀不到與權限不足四種狀態 | `Ssms22/UI/SqlStateSurface.cs`，狀態與文案在 `SqlSurfaceState.cs` |
| 面板裡的一行狀態（正在讀取或讀不到，含轉圈停轉規則） | `Ssms22/UI/SqlBusyNotice.cs` |
| 打字與選取的去彈跳長度（搜尋、預覽、估算） | `Ssms22/UI/SqlAssistChrome.Delays.cs` 的 `Debounce` |
| 對話框的資訊列、分段、分段卡片、選項列、頁尾與破壞性主要動作 | `Ssms22/UI/SqlAssistChrome.Dialogs.cs` |
| 對話框殼層（標題、尺寸、主題、字型、置中） | `Ssms22/UI/SqlAssistDialogs.cs` |
| 獨立 SQL 唯讀預覽／著色編輯 | `Ssms22/UI/SqlReadOnlyViewer.cs`／`SqlTextEditor.cs`（外觀由呼叫端掛 `SqlScriptTheme`） |
| 文字標記的底色與字色（命中兩級、區塊端點的預設）；大面積分類色不走它，見[文字標記](text-marks.md) | `Ssms22/UI/TextMarkColors.cs`（命中那一組在 `MatchPalette.cs`） |
| 「上一處／第幾處／下一處」的按鈕與讀數（沒有鍵盤捷徑，理由見[命中高亮](search-highlight.md)） | `Ssms22/UI/SqlMatchNavigator.cs`（狀態在 `Core/Matching/MatchCursor.cs`，捲動由呼叫端做） |
| SQL 著色分類、原文選取映射與編輯器主題適配 | `Ssms22/Preview/SqlScriptDocument.cs`（`Classify`）／`SqlScriptTheme.cs` |
| WPF 資料格的選取匯出、顯示順序與空欄讀值 | `Ssms22/UI/SqlDataGridText.cs` |
| SQL 圖示（補全、結構預覽與 QuickInfo 的原生圖示及快取） | `Ssms22/UI/SqlIcons.cs` |
| 自製 UI 的語意圖示與 moniker 對照、原生影像插槽 | `Ssms22/UI/SqlIcon.cs`、`SqlIcons.Images.cs`、`SqlIconImage.cs` |
| 佈景主題筆刷；過濾面板的面板與排序選單不在宿主視覺樹上，要一起套 | `Ssms22/UI/VsThemeBrushes.cs` 的 `Apply` |
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
