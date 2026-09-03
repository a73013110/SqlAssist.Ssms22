# 依變更範圍讀取的硬規則

範圍：原本放在根目錄的功能禁令，保留原文與理由。它們仍是必要規範，不是選讀建議。
先由 [索引](index.md#修改前必讀的護欄) 選對章節；跨範圍修改讀取所有相關章節，
測試則依被測程式的範圍選擇。不要每次把本檔與所有功能文件一併讀入。

## 設定

新增一個設定必須同時動四處，漏掉後三處的任何一處都是建置失敗（不是執行期回退）：
`SqlAssist.registration.json`、`Core/Settings/SqlAssistSettings` 屬性、
`SqlAssistMonikers` 常數、`SqlAssistSettingsReader.Read()` 對應。

- **禁止**手寫 moniker 清單。`SqlAssistMonikers.All` 由反射產生。
- **禁止**讓註冊檔的 `default` 與 POCO 的屬性預設值分歧。
- **禁止**讓 `enableWhen`／`visibleWhen` 跨分類參照——殼層會安靜地讓整個設定頁消失。
- **禁止**讓設定的 `enableWhen` 參照一個以上的設定；同分類也不行，那一項會安靜地消失。
  設定頁的縮排就是照這個參照排的，所以參照誰＝排在誰底下。
- **禁止**改動既有 `enum` 的字面值；那等於讓所有使用者的設定回退到預設。
- **禁止**把清單型資料放進 Unified Settings，它只收 bool／int／enum／string。
- **禁止**在取不到 Unified Settings 服務時讓擴充停擺；一律回退到內建預設值。

## 平台接線

- **禁止**在背景工作裡直接改編輯器緩衝區。非同步替換一律走
  `Editor/TextViewEditCoordinator`：切 UI 執行緒、檢查編輯器已關閉、從
  `ITrackingSpan` 取最新範圍、確認原文還在原處，少一道就會覆蓋使用者的輸入。

- **禁止**在 Ssms22 的平台邊界自己寫 `try`／`catch`——MEF 建立方法、編輯器事件、
  按鍵處理常式、派送佇列上的工作、沒有人接結果的背景工作，一律走
  `SqlAssistPlatformGuard`。三族方法的分工與四種例外情形見
  [docs/architecture.md](architecture.md)。

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
- **禁止**用現代編輯器的 `ICommandHandler` 接殼層命令（F12 之類）。SSMS 的查詢視窗
  在核心編輯器外面還有自己的文件檢視與舊版語言服務，命令到不了現代管線——實測
  F12 連一行紀錄都沒有。兩條會動的路：命令表的鍵繫結（SSMS 22 沒有把 F12 綁在
  `Edit.GoToDefinition` 上，所以那才是 F12 真正走的路），以及
  `Editor/SqlShellCommandFilter`（`IVsTextView` 上的 `IOleCommandTarget`，插在鏈
  最前面）。濾鏡**必須**在 `QueryStatus` 回報 supported＋enabled：沒有人認領的命令
  是停用的，停用的命令連 `Exec` 都不會發出去。那條路徑每個按鍵都會走過，**禁止**在
  轉傳之前做 GUID 比對以外的任何事。詳見
  [docs/go-to-definition.md](go-to-definition.md)。
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
  詳見 [docs/development.md](development.md)。

## 中繼資料

- **禁止**讓 `DbException` 冒出 `SqlMetadataCatalog`。連不上、逾時、權限不足在
  `TryLoad` 降級成「這一輪沒有資料」；冒出去會讓平台邊界每按一次鍵記一份完整堆疊。
  只接 `DbException`，失敗不進快取，理由見 [docs/metadata.md](metadata.md)。

- **禁止**在資料不齊時輸出半份可以執行的東西。種類問
  `SqlObjectKinds.HasExecutableScript`、這一次查到的資料問
  `SqlObjectStructure.CanBuildExecutableScript`，任何一道不過就整段換成註解，
  寫明缺什麼、兩個可能的原因與查得到的部分（格式只有 `BuildUnavailableScript` 一份）。
  查詢成功卻一列都沒有回來是常態不是例外：物件清單是快取的，中繼資料的可見度
  照權限過濾。少了欄位的 `CREATE TABLE` 只剩一對空括號，卻仍然貼得上去，
  理由見 [docs/metadata.md](metadata.md)。

## 片段與解析

- **禁止**在 Snippet 樣板裡把結構描述與物件名稱拆成 `$schema$.$object$` 兩格。
  第一格的答案幾乎永遠是 `dbo`，而建議清單依設定插進來的 `[dbo].[Lib_Reader]`
  這種寫法根本填不進拆開的格子。

- **禁止**為 Snippet 欄位另外宣告「這一格要列哪一類物件」。那份判斷在
  `SqlCompletionContextAnalyzer`，它讀的是實際文字；多一份宣告的症狀是樣板改了、
  宣告沒改，而清單靜靜地不再出現。

- **禁止**把 Snippet 欄位的上下文一律截到該格起點。只有「整格還是樣板填的預設值」
  那一次要當它不存在；使用者一打字，那幾個字就是前綴，而那是無限定字的格子
  （`INSERT (|)`）唯一的參與條件。截點只有
  `SqlSnippetExpansionController.ResolveAnalysisEnd` 一份，排名器也要照同一條
  把預設值視為空前綴，否則 Tab 進去的清單會被自己的預設值濾光。

- **禁止**再寫一份 SQL 註解略過或括號配對。`Core/Parsing` 的 `SqlTrivia` 與
  `SqlTokenNavigator` 是唯一出處；自己寫的那一份漏掉巢狀註解已經發生過一次。

- **禁止**做部分展開。`SELECT *` 只要有一個來源解析不出來就完全不展開；
  少幾個欄位的 `SELECT` 執行得動卻執行出錯的結果，比什麼都不做糟。
