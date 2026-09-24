# 通知呈現與驗證

通知島、浮層、動畫、設定頁版面與實機驗收。資料模型、合併與統計見[通知提示](notifications.md)；
三軸與可見度見[可見度](notifications-visibility.md)；文案見[通知訊息](notifications-messages.md)。

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
| 畫面本身 | `UI/NotificationIsland`、`NotificationRow`、`NotificationPromptView`、`NotificationActivityStrip` |
| 對齊基準、狀態圖示、換字 | `UI/NotificationLayout`、`NotificationStatusIcon`、`NotificationTicker` |

島嶼只認得 `UI/NotificationActivityItem` 與 `UI/NotificationPromptItem`，不認得 `Core/Notifications`。

## 控制器

整個處理程序一份，套件初始化時接上主視窗：通知、設定、主題、焦點移動各訂閱一次，計時器
一個（100 ms）。擁有者換了先隱藏、換 `Owner`、重新定位，島嶼從圓點重新長出來。沒有看得到的錨點時
立刻隱藏，不問通知來源——一問就會跑到期清理。平台邊界一律走 `SqlAssistPlatformGuard`。只剩提醒時
計時器停著，換框架靠焦點移動排一輪。

早退：沒有東西要顯示時不跑計時器；只剩提醒、又沒有等著發生的停駐轉換時計時器也停，提醒不會
自己到期。內容與形態都沒變時不重畫。浮層不在畫面上時，主題與焦點移動的事件不排程刷新；
通知與設定一律排程，新內容才出得來。

活動的叉號是全域的「這一批我看完了」，只隱藏目前批次，不取消工作，也不影響提醒。滑鼠停留或
鍵盤焦點在島嶼上時暫停活動的期限，移開後續跑剩餘時間。提醒不等顯示延遲。

## 浮層

- WPF `Window`：`WindowStyle=None`、`AllowsTransparency`、`ShowActivated=false`、`ShowInTaskbar=false`、
  `Topmost=false`、`ResizeMode=NoResize`，擴充樣式加 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`。
- 大小固定為 `NotificationIsland.MaxExtent`（320×352 DIP）加四周 16 DIP 柔影邊距；變形只在視窗裡面
  發生。島嶼收場播完才 `Hide()`，閒置時分層視窗不參與合成。
- 右距 16、距狀態列 12 DIP。狀態列高度從主視窗的視覺樹找（型別名含 `StatusBar`、貼著底邊），
  找不到或是拆出去的框架時用 28 DIP。擁有者移動、改大小、換 DPI 時只重新定位。
- 點擊穿透：透明像素本來就不收滑鼠；`WM_NCHITTEST` 以島嶼的命中測試判斷，形狀以外（含柔影、
  圓角外）回 `HTTRANSPARENT`。
- 按鈕要求焦點會讓浮層被啟用、搶走查詢視窗的游標，所以不在鍵盤模式時在 `PreviewGotKeyboardFocus`
  擋下焦點，點擊照常送達。
- 「聚焦通知」命令暫時啟用浮層、焦點放到第一個控制項，Tab 在島嶼裡繞圈；Esc 把焦點還給原本的
  元素，島嶼照一般的收回延遲收起。擁有者關閉前（`Closing`）先放手，附屬視窗才不會跟著被關掉。

## 通知島

- 形態：

  | 形態 | 何時 |
  |---|---|
  | Hidden／Compact／Done | 沒有內容／有工作在跑／都結束了 |
  | Expanded | 停駐 300 ms、鍵盤焦點進來，或按附條暫看；移開 600 ms 收回 |
  | Prompt／PromptWithActivity | 一則提醒；有活動時卡片底部多一條附條 |
  | PromptStack | 兩則以上提醒，有活動時最上面那一張帶附條 |

  預設就是膠囊，沒有「預設展開明細」。失敗不自動展開：圖示換警告，每多一個失敗短震一次。
  有提醒時停駐不展開活動。
- `UI/SpringMotion` 同時驅動寬、高與圓角（response 0.38 s、阻尼 0.82，逐幀積分、保留速度、靜止即取消
  `CompositionTarget.Rendering`；動畫關著直接到位）。膠囊高 32、圓角 16、寬 160–320；展開與提醒寬 320、
  圓角 14。內容依目標尺寸排版、貼著表面上緣，由圓角裁切露出，變形中不重排。換內容時舊的 120 ms 淡出，新的延遲
  60 ms、180 ms 淡入並從 0.96 放大；出現從 12 DIP 圓點長出，消失縮回圓點再淡出。
- 對齊：抬頭、文件列、各列、提醒卡與附條共用 `NotificationLayout` 的基準——左距 12、圖示中線 20、
  文字起點 32，右側文字與叉號筆畫收在右緣 16、按鈕外框收在右緣 10；抬頭列高 24，叉號 22×22 置中。
- 展開清單：抬頭「N 項工作 · 成功數/總數」字重 600（與多項膠囊同一句），有失敗時右側加「N 項失敗」
  標記；下方是文件列、3 DIP 進度條與明細（最高 240 DIP）。全部結束 400 ms 後進度條收成 1 DIP 髮絲線，
  有新工作時長回來。列的停駐與失敗底色往左右延伸 6 DIP，標題正常前景、「 · 主旨」淡色，`×N` 徽章不變。
  明細的捲軸握把只在捲動或停在握把上時出現，還能捲的那一端內容淡出 14 DIP。
- 附條（`NotificationActivityStrip`）：貼在卡片底邊、上緣一條髮絲線、整條可按。提醒卡上是活動摘要與
  「查看」，暫看時清單底部是「N 則提醒待處理」與「回到提醒」；活動結束時先淡出、收起後卡片才縮回。
  循環動畫同一時間只有一個：膠囊、清單抬頭或附條上的進度圈，各列的執行中是靜態光環。
  疊起來的提醒後兩層各露出 5 DIP、縮放 0.96／0.92，右上角「1/3」；處理掉一則時下一則滑上來。
- `UI/NotificationPromptView`：16 DIP 的 `SqlIcon` 語意圖示、標題字重 600、訊息最多 3 行（全文在 ToolTip）、
  按鈕次要在左主要在最右，叉號 ToolTip「稍後提醒」。提醒 `LiveSetting=Assertive`，活動 `Polite`；
  Tab 順序是叉號、按鈕列、附條。
- 動畫：展開時各列依序進場（每列延遲 30 ms、最多錯開 8 列、200 ms 從下方 8 DIP 淡入）；新列長出高度
  200 ms，離場先淡出 120 ms 再收起 160 ms；完成描出勾號 220 ms 再微彈 160 ms，失敗短震並淡入失敗底色；
  摘要換字時舊字上移、新字從下方進來（180 ms、6 DIP）。只在那一列狀態改變時播，不因別列更新重播；
  進度從目前值以 320 ms 接續。時長與緩動只有 `UI/NotificationMotion` 一份。
- 材質走 `SqlAssistChrome.ApplyNotificationMaterial`：柔影只掛在底色層並點陣快取，高對比退回實色。

## 設定頁

「通知與背景工作」分三段：呈現（啟用、材質、延遲、保留時間、詳細度）、十一個種類開關、結果通道
（失敗、部分成功）。moniker 一律 `sqlAssist.notifications.*`，子項都以單一同分類 `enableWhen` 掛
「顯示通知提示」。動畫由「一般」頁的全域動畫設定管。預設立即顯示、成功保留 2500 ms，失敗與降級
至少 6000 ms，最短可見 800 ms。

## 測試通知

「關於與診斷 → 通知失敗」上方一排按鈕，讓使用者自己確認通知看不看得到、長什麼樣子：3 秒後成功、
3 秒後失敗、連續 5 次成功（×N）、一則提醒、三則提醒（疊層）、提醒加活動（附條）。情境、標籤與時長
只在 `Core/Notifications/NotificationRehearsal`，新增一種只動那裡。

- 走正式的 `NotificationCenter`，可見度、合併、統計與最近失敗都是真的；失敗那一項列進同一頁，種類是
  「通知測試」（`NotificationKind.Diagnostics`，沒有開關）。
- 來源一律 `User`，詳細度擋不住；總開關或失敗通道擋下時狀態列說出是哪一格，不是按了沒反應。
- 活動用 `BeginDetached`，不成為環境父工作。提醒只有「知道了」，派送端登記空的處理常式。

## 驗證

自動測試涵蓋範圍看測試專案；渲染輸出在 `artifacts/theme-qa/notification-qa/`（`island-*`），不是 SSMS
宿主畫面，不能拿來宣稱實機通過。降級與診斷紀錄見[可見度](notifications-visibility.md#降級等級與診斷)。

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
