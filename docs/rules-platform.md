# 平台接線護欄

修改 `SqlAssist.Ssms22` 接線、事件、命令、UI、MEF、連線或部署前必讀。
- **禁止**在背景工作裡直接改編輯器緩衝區。非同步替換一律走
  `Editor/TextViewEditCoordinator`：切 UI 執行緒、檢查編輯器已關閉、從
  `ITrackingSpan` 取最新範圍、確認原文還在原處，少一道就會覆蓋使用者的輸入。

- **禁止**在 Ssms22 的平台邊界自己寫 `try`／`catch`——MEF 建立方法、編輯器事件、
  按鍵處理常式、派送佇列上的工作、沒有人接結果的背景工作，一律走
  `SqlAssistPlatformGuard`。三族方法的分工與四種例外情形見
  [平台 Guard](platform-guard.md)。

- **禁止**用 `Run` 記錄「會連續失敗」的平台探測（佈景筆刷、DPI、預先載入）；
  那要用 `Probe`／`BeginProbe`，否則紀錄檔會被灌滿而蓋掉真正的錯誤。

- **禁止**用 `SqlAssistPlatformGuard` 吞掉 Core 與 Metadata 的商業邏輯錯誤，
  也**禁止**用它處理「使用者按了卻沒反應」的失敗——工具選單的命令、預覽的狀態列、
  Snippet 管理員都要顯示訊息，每一句都不同。不走它的地方一律在該處註明理由。

- **禁止**在按鍵或滑鼠移動路徑上同步查詢資料庫。沒命中快取就這一輪不顯示，
  背景補上之後下一次就有。

- **禁止**在 QuickInfo 路徑向 SSMS 詢問目前連線——那個呼叫有 UI 執行緒相依性。

- **禁止**依賴 `CommitBehavior.Retrigger`：SSMS 22 的編輯器組件沒有任何一處讀它。
- **禁止**用 `DismissAllSessions` 搶 session。重開清單一律走 `SqlCompletionReopen`
  的三步驟（Dismiss → TriggerCompletion → OpenOrUpdate），一步都不能少。
- **禁止**在原地重開建議清單；必須排到派送佇列的 Background 優先權。
- **禁止**在浮動預覽裡內嵌真正的編輯器，或依賴 `ApplicationCommands.Copy` 的繞送。
- **禁止**在 `UI/SqlAssistChrome` 之外另立一套外觀。字型、字級推導、按鈕、輸入欄位、
  核取方塊與資料格樣板只有那一個來源；`Preview/PreviewChrome` 只放別的視窗用不到的東西。
  排版與視覺判準見[自製 UI 準則](ui-guidelines.md)。
- **禁止**用現代編輯器的 `ICommandHandler` 接殼層命令（F12 之類）。SSMS 的查詢視窗
  在核心編輯器外面還有自己的文件檢視與舊版語言服務，命令到不了現代管線——實測
  F12 連一行紀錄都沒有。兩條會動的路：命令表的鍵繫結（SSMS 22 沒有把 F12 綁在
  `Edit.GoToDefinition` 上，所以那才是 F12 真正走的路），以及
  `Editor/SqlShellCommandFilter`（`IVsTextView` 上的 `IOleCommandTarget`，插在鏈
  最前面）。濾鏡**必須**在 `QueryStatus` 回報 supported＋enabled：沒有人認領的命令
  是停用的，停用的命令連 `Exec` 都不會發出去。那條路徑每個按鍵都會走過，**禁止**在
  轉傳之前做 GUID 比對以外的任何事。詳見
  [殼層命令](shell-commands.md)。
- **禁止**改了 `Menus.vsct` 卻沒把 `ProvideMenuResource` 的版號加一，也**禁止**
  改完命令表後用 `Deploy-DebugExtension.ps1` 部署。殼層照 pkgdef 的
  `Menus.ctmenu, N` 決定要不要重讀命令表，而 pkgdef 不在部署清單裡，清快取也沒用。
  症狀與 MEF 快取同一類：新選單不出現、新綁的鍵沒反應，沒有例外也沒有記錄。
  改命令表一律走 `Install-Extension.ps1` 重新安裝。
- **禁止**讓命令自己算可見度（`BeforeQueryStatus` 設 `Visible`）卻沒在命令表標上
  `DynamicVisibility` 與 `DefaultInvisible`。殼層只有看到前者才理會 `QueryStatus`
  回報的「隱藏」，否則項目照樣出現在選單上——沒有例外、沒有紀錄，而且從程式碼上
  看完全正確。`tools/Test-CommandTable.ps1` 會比對兩邊。
- **禁止**搬動 MEF 匯出型別（`[Export]`、`[Export(typeof(ICommandHandler))]`、
  `IWpfTextViewCreationListener`…）的命名空間之後，只把 DLL 部署過去就開始測。
  SSMS 的 MEF 快取記的是完整型別名稱，會**安靜地**讓那些部件建立失敗——沒有例外、
  沒有記錄，只有「功能整組消失」。`Deploy-DebugExtension.ps1` 已經每次都清快取，
  但**禁止**繞過它手動複製 DLL。判斷依據：記錄檔沒有「SQL 編輯器已建立」就是快取過期。
  詳見[偵錯](debugging.md)。
