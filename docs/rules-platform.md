# 平台接線護欄

修改 `SqlAssist.Ssms22` 接線、事件、命令、UI、MEF、連線或部署前必讀。
- **禁止**在背景工作裡直接改編輯器緩衝區。非同步替換一律走
  `Editor/TextViewEditCoordinator`：切 UI 執行緒、檢查編輯器已關閉、從
  `ITrackingSpan` 取最新範圍、確認原文還在原處，少一道就會覆蓋使用者的輸入。

- **禁止**在 Ssms22 的平台邊界自己寫 `try`／`catch`——MEF 建立方法、編輯器事件、
  按鍵處理常式、派送佇列上的工作、沒有人接結果的背景工作，一律走 `SqlAssistPlatformGuard`：

  | 方法 | 用在哪 | 失敗時 |
  |---|---|---|
  | `Run`／`RunAsync`／`Create` | MEF 建立、按鍵、編輯器事件、派送工作 | `WriteAlways` 完整堆疊並回傳替代值 |
  | `Probe` | 佈景筆刷、DPI、游標位置、錨點座標等會連續失敗的可選探測 | 只在詳細診斷記一行 |
  | `Begin`／`BeginProbe` | 沒有人接結果的背景工作；後者用於預載、預熱，以及逾時放掉等待、仍在跑的工作（傳 `Task`，不必壓 VSTHRD003） | 依前兩族的層級處理 |

  **禁止**用 `Run` 記錄會連續失敗的探測，紀錄檔會被灌滿而蓋掉真正的錯誤。取消通常視為正常
  結束；只有 `RunPropagatingCancellation` 必須把取消狀態交回平台，否則過期內容會被當成有效答案。
  替代值用 `Func<T>`，成功時不必先算昂貴的完整候選清單。

- **禁止**用 Guard 吞掉 Core 與 Metadata 的商業邏輯錯誤。以下四類刻意不走 Guard，並在該處註明理由：
  使用者主動觸發、失敗必須看見的（工具命令、預覽狀態列、片段管理員與 F12，每一句訊息都不同）；
  `SqlAssistPackage` 載入失敗（記錄後重擲，讓殼層知道套件未載入）；有例外篩選的預期失敗（例如
  片段存放區只接檔案系統錯誤）；`SqlAssistDiagnostics` 本身（Guard 的錯誤正要寫到這裡）。

- **禁止**在按鍵或滑鼠移動路徑上同步查詢資料庫。沒命中快取就這一輪不顯示，
  背景補上之後下一次就有。

- **禁止**在 QuickInfo 路徑向 SSMS 詢問目前連線——那個呼叫有 UI 執行緒相依性。

- **UI 親和性由被呼叫的那一端自保。**包裝宿主服務的非同步方法進場就
  `SwitchToMainThreadAsync`（已經在上面時同步完成，不花錢），**禁止**改成「在回傳前切回去
  讓呼叫端接手」：續程落在哪一條執行緒是呼叫端的 `await` 決定的，一個
  `ConfigureAwait(false)` 就把那個保證作廢，而且只在中間真的 await 過的那幾條路上發作。
  同步方法切不了執行緒，維持 `ThrowIfNotOnUIThread`。自保的那幾支**禁止**用
  `JoinableTaskFactory.Run` 同步等待。完整推導見[結果導航](search-navigation.md)的執行緒分工。
  這一條現在由 `VSTHRD109` 在編譯期擋著：非同步方法裡寫 `ThrowIfNotOnUIThread` 直接是
  error。分析器開了哪幾條、關了哪幾條與理由見 `.editorconfig`。

- 要把續程留在 UI 執行緒時**明寫 `ConfigureAwait(true)`**：這個專案滿是
  `ConfigureAwait(false)`，留空的那一個看起來像漏掉的。

- **禁止**依賴 `CommitBehavior.Retrigger`：SSMS 22 的編輯器組件沒有任何一處讀它。
- **禁止**用 `DismissAllSessions` 搶 session。重開清單一律走 `SqlCompletionReopen`
  的三步驟（Dismiss → TriggerCompletion → OpenOrUpdate），一步都不能少。
- **禁止**在原地重開建議清單；必須排到派送佇列的 Background 優先權。
- **禁止**在浮動預覽裡內嵌真正的編輯器，或依賴 `ApplicationCommands.Copy` 的繞送。
- **禁止**在 `UI/SqlAssistChrome` 之外另立一套外觀。字型、字級推導、按鈕、輸入欄位、
  核取方塊與資料格樣板只有那一個來源；`Preview/PreviewChrome` 只放別的視窗用不到的東西。
  排版與視覺判準見[自製 UI 準則](ui-guidelines.md)。
- **禁止**用現代編輯器的 `ICommandHandler` 接殼層命令（F12 之類）：命令到不了現代管線。
  走命令表的鍵繫結或 `Editor/SqlShellCommandFilter`；濾鏡**必須**在 `QueryStatus` 回報
  supported＋enabled，且在轉傳之前**禁止**做 GUID 比對與一次靜態旗標讀取以外的任何事——
  每個按鍵都走過那裡。理由與排查見[殼層命令](shell-commands.md)。
- **禁止**改了 `Menus.vsct` 卻沒把 `ProvideMenuResource` 的版號加一，也**禁止**改完命令表後
  用 `Deploy-DebugExtension.ps1` 部署，一律 `Install-Extension.ps1` 重新安裝：pkgdef 不在部署
  清單裡，新選單與新鍵繫結會安靜地不生效。
- **禁止**讓命令自己算可見度（`BeforeQueryStatus` 設 `Visible`）卻沒在命令表標上
  `DynamicVisibility` 與 `DefaultInvisible`：殼層會照樣顯示，沒有例外也沒有紀錄。
  `tools/Test-CommandTable.ps1` 會比對兩邊。
- **禁止**搬動 MEF 匯出型別的命名空間後繞過 `Deploy-DebugExtension.ps1` 手動複製 DLL：MEF 快取
  記完整型別名稱，部件會安靜地建立失敗。記錄檔沒有「SQL 編輯器已建立」就是快取過期，
  見[偵錯](debugging.md)。
