# SQL Memory：產品與架構契約

SQL Memory 是同一個停駐工具窗中的 **History** 與 **Favorites**。History 保存執行與草稿，
Favorites 是使用者明確收藏的 SQL；收藏不等於檔案儲存，也不代表執行。
操作見 [UI](sql-memory-ui.md)，資料見[儲存](sql-memory-storage.md)，清理見[維護](sql-memory-maintenance.md)與[用量](sql-memory-usage.md)。

## 分層

分層依相依決定，各專案職責見[架構](architecture.md)；資料夾、命名空間與型別一律
`SqlMemory` 前綴，產品入口與文案同樣只有 SQL Memory。UI 不持有 store，所有 I/O 經宿主背景入口。

## 文件、版本與執行

`SqlDocument` 不綁連線；`SqlSession` 對應一次編輯器生命週期。同一路徑可共用 DocumentId，
新視窗必須有新 SessionId。未存檔視窗各自建立文件身分，不以 `SQLQuery1.sql` 等標題當主鍵。
每次擷取前重讀路徑：第一次存檔或另存後，舊 Session 以當下內容正式關閉，新 Session 掛在新路徑的文件上。
舊 Session 從未擷取就不送關閉，避免替暫存標題造出版本。交接不等下一次輸入：存檔／另存／改名
（`ITextDocument.FileActionOccurred`）在身分過期或尚有未記下的輸入時立刻擷取，離開視窗時去彈跳未到期也立刻擷取，
否則此時異常終止會讓 Recovery 永遠掛在暫存標題上。連線改變不補擷取：內容未變不重寫 Recovery。

`QueryContent` 精確雜湊 UTF-16LE code units，內容位址有演算法前綴；不正規化空白、大小寫、
換行、NUL 或未配對 surrogate，不使用 delta chain。去重命中的驗證策略與全文讀取的完整性
保證見[儲存](sql-memory-storage.md)。

- idle 以每 Session 一份 Recovery 保存最新全文；內容改變且跨過設定間隔才建立 auto revision。
- 執行事件獨立於 Revision；重複完整 SQL 或連續相同選取 SQL 可重用版本。
  同一 Session 連續相同的執行只佔一列 History，規則見[合併](sql-memory-search.md#連續執行合併)。
  選取版本不改文件 head、不覆蓋整份 Recovery；連線取自執行當下的快取。
  方塊選取或多重選取依文件順序、以文件換行串接各範圍，不記錄範圍之間沒有執行的文字。
- 關閉先保存最終版本，再於同一交易刪除 Recovery；晚到 idle 不得重新開啟 Session。
- Favorites 與擷取獨立。`SqlFavorite` 引用擷取產生的不可變 Revision，或自己建立一份不屬於任何 Session
  的版本；後者讓查詢視窗與未存檔草稿在擷取關著時也收得起來，且不寫 History。
  收藏本身就保護目前版本，不需要「收藏中的收藏」。

`CommitAsync` 在一個交易完成內容去重、版本／執行、Session head、Recovery 及 CaptureId。
先判斷 CaptureId 重送，再 CAS Session.Version；Session.Sequence 決定擷取順序，不用時間排序取代。

## 擷取與生命週期

`sqlAssist.sqlMemory.enabled` 與 SqlAssist 總開關都開啟才運作；兩者預設都是開的，
第一次真正接上儲存時送一則通知說明資料在哪裡、怎麼關掉。停用時不開資料庫、不讀舊資料、
不擷取也不維護。設定項與預設值見[設定](settings.md)。

`SqlMemoryRuntime` 管理儲存、writer、租約心跳及背景維護排程，狀態改變以事件通知工具窗：

- `ITextBuffer.Changed` 重排 idle 去彈跳；`ITextView.Closed` 擷取最後內容。
- 殼層濾鏡以 `IVsCmdNameMapping` 解析 `Query.Execute`，只記錄訊號後轉交命令。
  無法解析會記錄診斷，草稿／關閉擷取仍運作；不得宣稱已追蹤執行。
- 熱路徑只檢查旗標、記時間及排程；不可變 `ITextSnapshot` 到背景才展開全文。
- 連線事件在 UI 執行緒更新快取；按鍵與 QuickInfo 路徑不向 SSMS 同步問連線。
  只保存名稱，不保存連線字串、密碼或 token。

writer 最多接受 64 筆／32 MB 文字估計，包含處理中的項目；同 Session 的待處理 idle 可合併，
Execute／Close 是屏障。拒收必須可見，不淘汰已接受的執行事件。文字估計不是程序記憶體上限。
processor 對 CAS 衝突與儲存 Busy 各做有界重試，退避期間不持有宿主或隔離層閘門。Busy 重試用盡只放棄該筆並經回呼可見，
writer 繼續；損毀、不相容或未知錯誤才使 writer fault 並停止接受。強制結束程序仍可能失去未落盤內容。

宿主閘門只包開啟、換設定與關閉，排隊的轉換收斂到最新設定；UI、手動整理、維護與心跳不經過它。
關閉先取消該儲存的讀取、心跳與維護，排空 writer、交回維護租約，等隔離層進行中的操作離開才卸載 AppDomain；
關閉 SSMS 時總共只等 5 秒，逾時記錄診斷並放棄剩餘擷取。
手動整理先等本程序 writer 閒置；VACUUM 期間提交遇 Busy 由 processor 退避，讀取照常。
每次開庫或關庫更新宿主世代，舊成功／失敗回應都不得污染新頁面；途中被關閉的讀取以
`Unavailable` 分類回報。驗證邊界見[驗收](sql-memory-validation.md)。
