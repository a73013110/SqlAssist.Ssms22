# SQL Memory：互動與視覺

工具選單／toolbar 的 **History**、**Favorites** 聚焦同一個 **SQL Memory** Tool Window。
殼層保存停駐位置；沒有獨立預覽視窗或第二份位置設定。樣式遵守 [UI 準則](ui-guidelines.md)。

## 主從瀏覽

- 共用一份 Browser、`SqlMemoryList`、卡片模板、搜尋、連線 facets 與選取流程；頁籤只決定查詢及可用操作。
- 上方清單、下方 Preview，預設 3:2。水平 splitter 可拖曳，聚焦後 ↑／↓ 每次調整 16 DIP。
  收合保留目前選取及調整比例，清單取得空間；展開讀取當前選取。不因視窗變窄突然交換閱讀方向。
- 滑鼠單擊或 ↑／↓ 選取即預覽；220 ms 去彈跳後非同步讀全文。切換立即清除舊 SQL 並顯示載入狀態，
  取消、選取識別及宿主世代共同擋住過期成功／失敗。收合、隱藏、停用與 Dispose 都取消讀取。
- 雙擊或卡片上的 Enter 才開啟新 Query；按鈕的 Enter 只執行該按鈕，不額外開窗。
  右鍵先選取該列；選取本身永遠不開 Query、不執行 SQL。
- 每頁 50 筆，筆數與載入更多位於可捲動清單底部；向下捲至底端可自動續頁，保留手動按鈕。
  頁尾不參與 SQL 選取，清單保持 recycling virtualization；版面或資料增高不自行觸發續頁。
  重整可恢復仍在第一頁的選取，沒有選取時預覽第一筆，但不搶鍵盤焦點。
- SQL card 只顯示單行摘要，名稱、狀態、時間及精簡連線 badge；完整 SQL 在 Detail，截斷資訊可用 Tooltip。
  卡片快速操作僅複製、開新 Query、Add to Favorites；沒有另一個「開預覽」動作。

Preview 使用共用 `SqlReadOnlyViewer`／`SqlScriptDocument`，支援 SQL 著色、選取、Ctrl+C、
全文複製及顯示換行。顯示與原文分離；換行切換不重寫原文，主題刷新不重新查庫。
空清單、載入、空 SQL、內容已回收、停用與失敗皆有明確狀態，不留上一筆 SQL 冒充目前內容。
清單與 Preview 共用 `SqlLoadingSurface` 表面載入圖示，不為載入文字保留頁尾；隱藏時停止動畫，
介面動畫關閉時改用靜態圖示。Preview 摘要依狀態、名稱、伺服器、資料庫、時間排序，狀態與連線
共用卡片膠囊。左側 Preview 固定，右側資訊保持單行、不顯示捲軸；資訊列上的滾輪向下往右、向上往左（即使沒有溢出也不傳給 Preview），
聚焦後可用 ←／→、Home／End。收藏未提供時間與來源執行狀態，保留「收藏」標示而不推測。

## 篩選與工具列

History 預設全部種類、七天、所有連線；狀態與期間是獨立群組，以淡色分隔線區分，窄窗換行。
Favorites 使用 Global／Server／Database scope，不顯示期間或執行狀態，也不合併父層收藏。
無關的 Server／Database section 隱藏，不用停用灰色控制項佔空間。

搜尋獨佔一列，內嵌放大鏡／清除，Ctrl+F 聚焦；搜尋語意與掃描預算見[搜尋](sql-memory-search.md)。
一頁因預算提早結束時，清單頁尾顯示「已搜尋至 yyyy/MM/dd（本機日期），繼續搜尋可再往前找」，
Favorites 顯示「已搜尋部分收藏」；「載入更多」改名「繼續搜尋」，由使用者按下才續搜。
該頁即使沒有命中也不顯示「沒有符合條件」；篩選或搜尋變更時清除進度。
目前連線一次套用 Server／Database，Favorites 同時切 Database scope；無連線保留篩選並提示。
此操作只篩選，不切換 SSMS 連線。

Server／Database Header 只負責 disclosure，不畫成已選取 pill。Chevron 有固定 slot、左右間距，
收合只旋轉繪圖不改量測；預設收合，已選條件以同列摘要顯示並保留 Tooltip。
展開後名稱 pills 獨佔全寬、最多兩列高度，避免窄窗 Header 對齊到多列選項的中間。
名稱有獨立的最近／最早／名稱排序與續頁。

Pills、badge、toolbar 的 icon／文字使用同一視覺中心線，內外垂直 padding 對稱；互動狀態不改版面見
[UI 準則](ui-guidelines.md)。工具列窄窗收起文字，圖示仍有 Tooltip 與 automation name；連線篩選不是主要動作。
高對比保留配對選取文字，不只替背景換色。
Tabs、篩選與卡片圖示由 `SqlAssistChrome.MemoryOptionIcon` 依語意值選取 `SqlIcon`；排序按鈕與選單共用同一對應，
History／Favorites 與工具列同源。卡片動作以 `SqlMemoryRowAction` 識別，不拿圖示當動作。
文字讀所屬控制項的動態前景，避開宿主呈現器樣式干擾。
卡片快速操作使用透明底，文字與圖示跟隨卡片的 hover／selected 配對前景，不另畫不透明操作區。

## 收藏與開啟

已有 Revision 的 History 可 **Add to Favorites**；Recovery 尚無版本，停用並說明先開新 Query。
收藏提供編輯名稱／說明／scope、編輯 SQL，以及 **Remove from Favorites**。
移除必須確認，取消是預設；不連帶刪除 History。

metadata 與 SQL 各用一個對話框：前者不讀全文，後者使用共用純文字 editor。
未存 SQL 關閉前確認捨棄；提交期間不允許關閉，Conflict 或不明回應保留輸入且不盲目重送。

開新 Query 沿用目前 SSMS 連線，不採用歷史／收藏 scope，也不直接執行。
只透過 `SsmsScriptWindow`／`TextViewEditCoordinator` 寫入剛建立且仍空白的編輯器；失敗仍可複製 SQL。

本版不提供舊版本列表／還原、覆蓋目前 Query、批次刪除、跨 scope 合併或任意 SQL 直接新增收藏。
自動 WPF 渲染與 SSMS 實機驗收的範圍見[驗收](sql-memory-validation.md)。
