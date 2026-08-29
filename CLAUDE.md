# SqlAssist for SSMS 22

SSMS 22.9.x 的 T-SQL 擴充。三個專案：`SqlAssist.Core`（netstandard2.0，零 VS 相依）、
`SqlAssist.Metadata`（netstandard2.0，只依賴 `System.Data`）、`SqlAssist.Ssms22`（net48 VSIX）。

細節按需查 [docs/](docs/)：`architecture`、`completion`、`snippets`、`wildcard-expansion`、
`structure-preview`、`settings`、`metadata`、`development`。**動手前先讀對應的那一份**，
下面每一條禁令背後都有一次踩過的坑，理由寫在文件裡。

## 分層

- **禁止**讓 `SqlAssist.Core` 或 `SqlAssist.Metadata` 參照 Visual Studio／SSMS 的組件。
- **禁止**把只看文字就能判斷的邏輯寫進 `SqlAssist.Ssms22`——那裡跑不了單元測試。
  Ssms22 只做接線：拿服務、掛事件、把結果寫回編輯器。
- **禁止**在 `Core/Matching` 參照 `Core/Completion`。Matching 是與領域無關的字串比對。

## 資料夾與命名

- **禁止**資料夾與命名空間不一致。`src/X/Foo/` 一律是 `X.Foo`，測試專案鏡像同一份路徑。
- **禁止**為單一檔案開資料夾。
- **禁止**用相對命名空間限定（`Metadata.SqlObjectInfo`）；一律 `using` 加簡名。
- **禁止**手動編輯 `Keywords/SqlKeywordCatalog.Generated.cs`。改
  `tools/Generate-Keywords.ps1` 後重跑，產物要進版控。

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

## 共用元件

同一件事寫成兩份時，症狀一律是「其中一份改了另一份沒改」，而且沒有任何徵兆。
共用元件的清單與各自的分岔症狀見 [docs/architecture.md](docs/architecture.md)。

- **禁止**在背景工作裡直接改編輯器緩衝區。非同步替換一律走
  `Editor/TextViewEditCoordinator`：切 UI 執行緒、檢查編輯器已關閉、從
  `ITrackingSpan` 取最新範圍、確認原文還在原處，少一道就會覆蓋使用者的輸入。
- **禁止**在 MEF 建立方法、編輯器事件與按鍵處理常式裡自己寫 `try`／`catch`；
  走 `SqlAssistPlatformGuard`。反過來，Core 與 Metadata 的商業邏輯錯誤**禁止**
  用它吞掉，工具選單的命令也不走它——那些要顯示訊息框，每一句都不同。
- **禁止**再寫一份 SQL 註解略過或括號配對。`Core/Parsing` 的 `SqlTrivia` 與
  `SqlTokenNavigator` 是唯一出處；自己寫的那一份漏掉巢狀註解已經發生過一次。
- **禁止**在工具腳本裡寫死 SSMS 路徑或擴充的 Identity Id；
  一律從 `tools/SqlAssist.Tools.psm1` 取，並支援 `-SsmsInstallDir` 覆寫。

## 按鍵與滑鼠路徑

- **禁止**在按鍵或滑鼠移動路徑上同步查詢資料庫。沒命中快取就這一輪不顯示，
  背景補上之後下一次就有。
- **禁止**在 QuickInfo 路徑向 SSMS 詢問目前連線——那個呼叫有 UI 執行緒相依性。
- **禁止**做部分展開。`SELECT *` 只要有一個來源解析不出來就完全不展開；
  少幾個欄位的 `SELECT` 執行得動卻執行出錯的結果，比什麼都不做糟。

## 平台

- **禁止**依賴 `CommitBehavior.Retrigger`：SSMS 22 的編輯器組件沒有任何一處讀它。
- **禁止**用 `DismissAllSessions` 搶 session。重開清單一律走 `SqlCompletionReopen`
  的三步驟（Dismiss → TriggerCompletion → OpenOrUpdate），一步都不能少。
- **禁止**在原地重開建議清單；必須排到派送佇列的 Background 優先權。
- **禁止**在浮動預覽裡內嵌真正的編輯器，或依賴 `ApplicationCommands.Copy` 的繞送。
- **禁止**在 `UI/SqlAssistChrome` 之外另立一套外觀。字型、字級推導、按鈕、輸入欄位、
  核取方塊與資料格樣板只有那一個來源；`Preview/PreviewChrome` 只放別的視窗用不到的東西。
- **禁止**搬動 MEF 匯出型別（`[Export]`、`[Export(typeof(ICommandHandler))]`、
  `IWpfTextViewCreationListener`…）的命名空間之後，只把 DLL 部署過去就開始測。
  SSMS 的 MEF 快取記的是完整型別名稱，會**安靜地**讓那些部件建立失敗——沒有例外、
  沒有記錄，只有「功能整組消失」。`Deploy-DebugExtension.ps1` 已經每次都清快取，
  但**禁止**繞過它手動複製 DLL。判斷依據：記錄檔沒有「SQL 編輯器已建立」就是快取過期。
  詳見 [docs/development.md](docs/development.md)。

## 程式碼與建置

- **禁止**留下編譯警告（`TreatWarningsAsErrors`），也**禁止**關掉 `Nullable`。
- **禁止**改回 VSTest 轉接層。`global.json` 已把執行器指定為 Microsoft.Testing.Platform，
  跑測試用 `tools\Run-CoreTests.ps1` 或 `dotnet test <方案>`。
- **禁止**寫「這行在做什麼」的註解。註解只寫**為什麼**：這樣選的理由、
  試過而失敗的做法、以及不這樣寫會出現的症狀。現有檔案就是範本。
- **禁止**用非繁體中文撰寫註解與文件。
- **禁止**把細節寫回 `README.md`；它只做入口與索引，內容進 `docs/`。
