# 通知呈現與驗證

本頁包含通知島的錨點、浮層、形態、設定頁與實機驗收；資料模型與合併見[通知提示](notifications.md)，
三軸與可見度見[可見度](notifications-visibility.md)。

## 分工

| 這件事 | 在哪 |
|---|---|
| 該顯示什麼：可見度、合併、措辭、活動的關閉 | `Notifications/NotificationPresenter.Island` |
| 形態：膠囊、展開、提醒、附條、疊層 | `Notifications/NotificationIslandState`（純邏輯） |
| 活動的延遲、最短可見、收場與到期 | `Notifications/NotificationLifecycle` |
| 何時顯示、唯一的計時器與訂閱 | `Notifications/NotificationIslandController` |
| 錨在哪個視窗 | `Notifications/NotificationAnchor`；框架與焦點移動在 `UI/SsmsWindows` |
| 透明附屬視窗、定位、點擊穿透、鍵盤模式 | `Notifications/NotificationOverlay` |
| 浮層在擁有者上的位置（裝置像素） | `Notifications/NotificationPlacement` |
| 提醒按鈕的派送 | `Notifications/NotificationActionRouter` |
| 畫面、對齊基準、時長與緩動 | `UI/NotificationIsland` 與同資料夾的 `Notification*`；時長只在 `NotificationMotion` |

島嶼只認得 `UI/NotificationActivityItem` 與 `UI/NotificationPromptItem`，不認得 `Core/Notifications`：
表面認得來源型別的話，第二種回饋來源得先變成一則通知才畫得出來。

## 錨點與控制器

- 島嶼錨在使用者正在操作的框架（主視窗或拆出去的框架）右下角、狀態列上方。焦點在別的程式、
  對話框或 WinForms 視窗上時沿用目前的錨點；錨點最小化、隱藏或關閉時改用主視窗。
- 不跟最後取得焦點的 SQL 編輯區：查詢視窗拆出去後回主視窗操作 SQL Search 或物件總管，通知會
  出現在被蓋住、在另一台螢幕或已最小化的框架上。對話框不當錨點，右下角是它們的「確定／取消」。
- 同一框架裡切換分頁、工具視窗與 F12 開新查詢視窗不換擁有者，島嶼不重播；換框架時先隱藏、
  換 `Owner`、重新定位，再從圓點重新長出來。
- 控制器整個處理程序一份，套件初始化時接上主視窗：通知、設定、主題、焦點移動各訂閱一次，
  計時器一個。沒有看得到的錨點時立刻隱藏，不問通知來源——一問就會跑到期清理。平台邊界一律走
  `SqlAssistPlatformGuard`。
- 早退：沒有東西要顯示、或只剩提醒且沒有等著發生的停駐轉換時計時器停著；內容與形態都沒變時不重畫。
  浮層不在畫面上時只有通知與設定排程刷新。
- 活動的叉號是全域的「這一批我看完了」，只隱藏目前批次，不取消工作，也不影響提醒。滑鼠停留或
  鍵盤焦點在島嶼上時暫停活動的期限，移開後續跑剩餘時間。提醒不等顯示延遲。

## 浮層

不掛在各視窗的 adornment 或 `AdornerLayer` 上：那樣沒有宿主時（還沒開查詢視窗、焦點在物件總管）
通知會被吃掉，每個新視窗也都要記得註冊。改用一個透明附屬視窗，下列設定各自回答一個獨立視窗的顧慮：

- `WindowStyle=None`、`AllowsTransparency`、`ShowInTaskbar=false`、`ResizeMode=NoResize`。
  `Topmost=false` 而設 `Owner`：永遠在擁有者上方、跟著擁有者被別的程式蓋住與最小化，Alt+Tab 只有擁有者；
  另外聽 `StateChanged` 明確隱藏與重新長出。獨立頂層視窗也不會被結果格線的 HWND 蓋住。
- 不搶焦點：`ShowActivated=false` 加 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`；不在鍵盤模式時於
  `PreviewGotKeyboardFocus` 擋下焦點，點擊照常送達，查詢視窗的游標不被搶走。
- 點擊穿透：透明像素本來就不收滑鼠；`WM_NCHITTEST` 以島嶼的命中測試判斷，形狀以外（含柔影、
  圓角外）回 `HTTRANSPARENT`。
- 大小固定為 `NotificationIsland.MaxExtent` 加柔影邊距，變形只在視窗裡面發生；收場播完才 `Hide()`，
  閒置時分層視窗不參與合成。
- 位置以裝置像素交給 `SetWindowPos`，邊距依擁有者 DPI 換算；擁有者移動、改大小、換 DPI 時只重新
  定位。狀態列高度從主視窗的視覺樹找（型別名含 `StatusBar`、貼著底邊），找不到或是拆出去的框架時用預設值。
- 「聚焦通知」命令暫時啟用浮層、焦點放到第一個控制項，Tab 在島嶼裡繞圈；Esc 把焦點還給原本的
  元素。擁有者關閉前（`Closing`）先放手，附屬視窗才不會跟著被關掉。

## 通知島

| 形態 | 何時 |
|---|---|
| Hidden／Compact／Done | 沒有內容／有工作在跑／都結束了 |
| Expanded | 停駐、鍵盤焦點進來，或按附條暫看；移開後收回 |
| Prompt／PromptWithActivity | 一則提醒；有活動時卡片底部多一條附條 |
| PromptStack | 兩則以上提醒，有活動時最上面那一張帶附條 |

- 預設就是膠囊，沒有「預設展開明細」。失敗不自動展開：圖示換警告，每多一個失敗短震一次。
  有提醒時停駐不展開活動。
- `UI/SpringMotion` 同時驅動寬、高與圓角，逐幀積分、保留速度、靜止即取消 `CompositionTarget.Rendering`；
  動畫關著直接到位。內容依目標尺寸排版、由圓角裁切露出，變形中不重排。出現從圓點長出，消失縮回圓點。
- 展開清單：抬頭與多項膠囊同一句，有失敗時右側加「N 項失敗」；下方是文件列、進度條與明細。
  全部結束後進度條收成髮絲線，有新工作時長回來。各區共用 `NotificationLayout` 的對齊基準。
- 附條（`NotificationActivityStrip`）整條可按：提醒卡上是活動摘要與「查看」，暫看時清單底部是
  「N 則提醒待處理」與「回到提醒」；活動結束時先淡出、收起後卡片才縮回。疊起來的提醒露出後兩層，
  右上角「1/3」；處理掉一則時下一則滑上來。
- 循環動畫同一時間只有一個：膠囊、清單抬頭或附條上的進度圈，各列的執行中是靜態光環。
  列動畫只在那一列狀態改變時播，不因別列更新重播；進度從目前值接續。
- `UI/NotificationPromptView`：`SqlIcon` 語意圖示、訊息最多 3 行（全文在 ToolTip）、按鈕次要在左
  主要在最右。提醒 `LiveSetting=Assertive`，活動 `Polite`；Tab 順序是叉號、按鈕列、附條。
- 材質走 `SqlAssistChrome.ApplyNotificationMaterial`：柔影只掛在底色層並點陣快取，高對比退回實色。

## 設定頁

「通知與背景工作」分三段：呈現（啟用、材質、延遲、保留時間、詳細度）、十一個種類開關、結果通道
（失敗、部分成功）。moniker 一律 `sqlAssist.notifications.*`，子項都以單一同分類 `enableWhen` 掛
「顯示通知提示」。動畫由「一般」頁的全域動畫設定管。預設立即顯示、成功保留 2500 ms，失敗與降級
至少 6000 ms，最短可見 800 ms。

## 測試通知

「關於與診斷 → 通知失敗」上方一排按鈕，讓使用者自己確認通知看不看得到、長什麼樣子：成功、失敗、
連續成功（×N）、一則提醒、疊層、提醒加活動。情境、標籤與時長只在 `Core/Notifications/NotificationRehearsal`。

- 走正式的 `NotificationCenter`，可見度、合併、統計與最近失敗都是真的；種類是「通知測試」
  （`NotificationKind.Diagnostics`，沒有開關）。
- 來源一律 `User`，詳細度擋不住；總開關或失敗通道擋下時狀態列說出是哪一格，不是按了沒反應。
- 活動用 `BeginDetached`，不成為環境父工作。提醒只有「知道了」，派送端登記空的處理常式。

## 驗證

渲染輸出在 `artifacts/theme-qa/notification-qa/`（`island-*`），不是 SSMS 宿主畫面，不能拿來宣稱實機通過。

### 尚未確認

以下都要在 SSMS 實機確認：

- 沒有連線、也沒開查詢視窗就按「檢查更新」：島嶼錨在主視窗右下。
- 啟動時自動檢查到新版：提醒出現；當天重開 SSMS 從快取再提醒一次；略過的版本不再出現。
- F12 開新查詢視窗：同一個擁有者，島嶼不重播。
- 文件拆到第二台螢幕：島嶼跟著點進的框架走（含只剩提醒時），回主視窗操作 SQL Search 就換回；
  那個框架最小化或關掉時回主視窗；焦點在對話框（含連線）或別的程式時留在原處。
- 最小化與還原：跟著擁有者隱藏與出現。
- 100%／150%／200% DPI，以及拖著擁有者跨螢幕：位置、大小與柔影清晰度。
- 高對比：實色、無柔影。減少動態效果：所有變形直接到位。
- 與結果格線（WinForms／HWND）重疊時島嶼在上面。
- 島嶼以外的區域（含柔影那一圈）點擊落到底下的編輯器與格線。
- 按提醒按鈕不會把查詢視窗的游標搶走；Alt+Tab 清單裡沒有浮層，切到別的程式時浮層被蓋住。
- 「聚焦通知」：Tab 繞圈、Enter 按鈕、Esc 把焦點還給查詢視窗。
- 多則提醒堆疊、提醒與活動並存時的附條、暫看與附條收起。
- 透明浮層跑彈簧動畫時的 CPU；狀態列高度偵測是否抓得到 SSMS 22 的狀態列。
