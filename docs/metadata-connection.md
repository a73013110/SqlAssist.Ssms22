# 目前連到哪個資料庫

中繼資料目錄跟著查詢視窗的連線走，而那條連線的資料庫會變：執行 `USE LibArchive`、
改工具列的下拉選單、變更連線、斷線後重連。問 SSMS 一次要一趟 UI 執行緒往返
（平常 2 到 7 ms，它忙的時候實測 1908 ms），所以按鍵路徑不問。

四種換法只有一個判斷式：`SqlMetadataService.IsConfirmationDue()`。等得起的呼叫端
先 `ConfirmConnectionAsync()` 再回答——建議清單、`SELECT *` 展開、F12 與參數提示
都跑在背景工作上，晚幾毫秒沒有代價。按鍵與滑鼠停留路徑照舊不等，只在背景排一次。
每多一種換法就補一次特別處理的話，漏掉的那一種會安靜地用舊資料庫的物件回答。

## 由 SSMS 的事件觸發

`Connections/SqlEditorConnectionWatcher` 訂閱 `ISqlEditorService` 的
`ConnectionChanged` 與 `ConnectionDisconnected`，收到就把對應的服務標成該重新確認。
前者由 `SqlScriptEditorControl.UpdateUIForNewCurrentDatabase` 觸發，它的三個呼叫者
就是三種換法：執行完一批查詢（`USE` 走這條）、工具列下拉選單、變更連線與重新連線。

事件在 UI 執行緒上，那裡只加一個計數；真的去問 SSMS 留在既有的背景路徑上。
每一次事件都寫進紀錄，哪一種換法觸發了哪一個事件只有那裡看得出來。

事件是全域服務的，服務卻是每個 `ITextView` 一份。對應關係在
`SqlCompletionServices.GetMetadataService` 建立，視窗關閉與服務釋放各解除一次，
套件卸載時由 `Shutdown()` 退訂。

## 指名問這一個視窗

`GetCurrentConnection()` 回答的是目前作用中那個視窗，而解析跑在背景工作上——
使用者切到別的分頁時，它會把別的視窗的連線寫進這一份服務。編輯器取得焦點時
在 UI 執行緒上記下 `GetActiveEditorMoniker()`，之後改用
`GetConnectionForSpecificQueryEditor` 指名問。不從文件路徑推識別：推錯的症狀是
事件永遠對不上任何一個視窗，而畫面上看不出差別。

指名問回 null 有兩種，用 `ListOpenedQueryEditorCaptionsWithMonikers` 分開：識別
還在就是這個視窗真的沒有連線，不得退回作用中視窗的連線；識別不在（另存新檔換掉了）
才退回去，並等下一次取得焦點換上新的。還沒取得過識別時同樣退回去。

## 另外兩道網

十秒一次的背景輪詢留著，涵蓋事件到不了的情形：取不到 `ISqlEditorService`、
訂閱失敗，或別的擴充繞過查詢視窗改了連線。

曾經有一條從指令碼文字讀出最後一個 `USE` 的訊號，事件接上之後整條移除了：
`ConnectionChanged` 已經涵蓋執行 `USE`，留著就是同一件事的第二套判斷，
而多一套判斷就多一處會漏。

**Ctrl+Shift+D** 除了清快取也讓每個查詢視窗重新確認連線。只清內容的話，
換過資料庫之後按下它，重新載入的仍然是舊資料庫的物件，而那正是使用者按它的原因。
