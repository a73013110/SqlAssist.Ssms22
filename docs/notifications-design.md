# 通知設計決策

`Core/Notifications` 的模型與命名決策。現況行為見[通知提示](notifications.md)，
文案見[通知訊息](notifications-messages.md)，三軸與可見度見[可見度](notifications-visibility.md)。
五步都已落地，本頁只留當初為什麼這樣選；行為改動一律改那三頁。

## 改名的理由

要回饋的不只背景工作：「片段已還原預設」「設定已重新載入」也該有回饋，`activity`
涵蓋不了。統一詞彙為 notification，moniker 前綴改 `sqlAssist.notifications.*`。
產品未公開發行，改前綴不必留搬遷規則。

## 分類拆成三軸

`BackgroundActivityKind` 把「做什麼」與「誰觸發的」塞進同一個 enum，造成兩個缺口：
使用者操作觸發的中繼資料查詢合併後繼承 `UserAction`，「中繼資料載入」開關看不到它；
`Other` 同時是「其他」與「漏分類」且預設隱藏，新工作靜默漏掉。

| 軸 | 值 | 用途 |
|---|---|---|
| `NotificationKind` | Metadata、Completion、Analysis、Preview、Editing、Navigation、Results、Snippets、Settings、Package、Unclassified | 子系統；十一類各有一格開關 |
| `NotificationOrigin` | User、Typing、Ambient、Startup | 觸發來源；決定跨不跨得過降噪門檻 |
| `NotificationLevel` | Trace、Debug、Info、Notice | 詳細度門檻 |

`Unclassified` 預設顯示。每個呼叫端三軸都要明寫，不吃預設值。

## 可見度依序判斷

1. 總開關關閉 → 隱藏
2. `Failed` → 看「顯示所有種類的失敗」
3. `Degraded` → 看「顯示部分成功」
4. 該 `Kind` 的開關關著 → 隱藏
5. `Level` 為 `Trace` 且詳細度不是「全部」→ 隱藏
6. `Origin` 為 `User` → 顯示
7. `Level` 為 `Notice` → 顯示
8. 其餘看詳細度門檻

`Kind` 排在 `Origin` 之前是第二次改的結果。原本 `User` 先放行，於是 Editing、
Navigation、Results、Snippets、Settings 這幾類的開關按了不會怎樣——它們的通知全由使用者
觸發。當時的結論是那幾類乾脆不給旋鈕，但只給一半更糟：使用者分不出「這裡沒有開關」與
「開關失效」。改成種類先問，十一格就都管得住自己那一類，`Origin` 與 `Level` 退回只管
降噪門檻。

Kind 開關以 `(Kind, moniker, 預設值, 標題)` 集中成 `NotificationKindToggle.All` 一張表，
十一類都有 moniker，新增一類只動註冊檔與該表：`SqlAssistSettings` 存
`NotificationKindSwitches` 位元遮罩、
`SqlAssistMonikers.All` 把表上的 moniker 併進來，兩邊都不逐項寫。逐項屬性的版本要同時動
POCO、moniker 常數、讀取端與可見度四處，每一處漏掉都沒有編譯錯誤。

詳細度下拉是四階，一比一對上 `NotificationLevel`：安靜 `Notice`、一般 `Info`、
詳細 `Debug`、全部 `Trace`。少了「全部」的話 `Trace` 就只是一個永遠看不到的等級。

## Degraded 與父子傳播

`Scope.Fail()` 目前沿 `_parent?.Fail()` 上傳，而 `TryLoad` 對 `DbException` 就標失敗，
於是一次可正常降級的查詢會讓整個使用者操作顯示為失敗，與中繼資料的降級語意矛盾。

改為子工作失敗只把父工作標成 `Degraded`，只有父工作自己的例外才是 `Failed`。
`Degraded` 對應 `Severity.Warning`，補上目前產生不出來的等級。

## 合併與統計是兩件事

畫面**合併**的鍵是 `(Kind, Title, Subject, Document, Source)`：只合併已完成且結果相同的項目，
執行中的各自成列以保留進度感，失敗與降級不併入成功，重複次數顯示為 `×N`。

診斷**統計**不受可見度影響：`Trace`、快取命中與被隱藏的一樣計入，依
`(Kind, Title, Subject)` 累計次數、總耗時、平均、最大、失敗與降級數，有上限並併入
「其他」。只開詳細度而不做統計答不出「哪些動作在重複」——畫面只會一片閃爍。

## 命名對照

`Activity` 一詞已從通知這條路徑上清掉，只有「最近活動」（`SqlAssistActivity`，記錄使用者
最後做了什麼）保留原意，與通知無關。

## 卡片與呈現端分開

卡片認得通知來源的型別時，第二個回饋來源（片段還原、設定重載）就得先變成一則通知才畫得出來。
改成卡片只認得 `UI/NotificationCardItem`，呈現端 `Editor/NotificationHost` 負責可見度、
合併與措辭；`NotificationAdornment` 只剩宿主與定位，卡片本身在 `NotificationSurface`。抽通用通知框架則相反——只有一個來源時
抽出來的抽象會照著那個來源長，等第二個來源出現時還是要重寫。

## 卡片在編輯區之間搬家

每個編輯區各一張卡片時，F12 開新查詢視窗或切分頁會讓同一份提示整個重建：列的身分沒了，
滑入、淡入與每一列的狀態動畫全部重播，看起來是提示消失了又跳出來。卡片改成全程一張，
換編輯區只是換掛到另一層。舊的先拔、接手的下一輪才掛，中間隔的是一次派送，因此
`NotificationHandover` 給一段寬限——沒有寬限就等於交接一定重播。

## 抬頭只回答文件

`Context` 一格同時裝檔名與資料庫名，於是一列資料庫名或一列沒有出處的背景工作就會讓抬頭
的檔名整個收掉，同一次查詢看起來是檔名時有時無。拆成 `Document` 與 `Source` 之後，抬頭
只看有文件的列，資料庫一律留在列上。

## 落地順序

五步都已完成：Core 模型與改名、設定改名與詳細度下拉、訊息目錄與全系統接線、
合併與工作階段統計、卡片解耦與效能。

相容多載已經移除：`Begin` 的三軸都沒有預設值，漏寫的接線點編譯不過。
`SqlAssistPlatformGuard.Begin` 同樣要求三軸；`BeginProbe` 維持不整批追蹤。
