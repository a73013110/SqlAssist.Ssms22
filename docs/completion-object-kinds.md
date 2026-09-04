# 函式與系統物件的補全範圍

## 資料表值函式與純量函式分開

`SuggestionKind.TableFunction` 與 `SuggestionKind.Function` 是兩類：前者是內嵌
（`IF`）與多語句（`TF`）資料表值函式，後者是純量函式。中繼資料層的
`SqlObjectKinds.IsDataSource` 早就這樣分了，只有建議項這一層曾經把三種函式壓成
同一類——症狀是 `FROM dbo.fn_` 之後整份清單一個函式都沒有，
而使用者看不出它和資料表有什麼不同。

分完之後三個位置各自對了：

- `FROM`、`JOIN`、`USING` 之後多列資料表值函式，純量函式仍然不列——
  它回傳的是一個值，放在那裡剖析不過。
- `APPLY` 之後**只**列資料表值函式。那個位置文法上要的是資料表值函式或衍生資料表，
  `CROSS APPLY dbo.Loan` 剖析得過卻沒有意義；而純量函式從前是連帶列出來的雜訊。
  認的是 `APPLY` 一個字，前面的 `CROSS` 與 `OUTER` 不改變後面要什麼。
- `ALTER FUNCTION`、`DROP FUNCTION` 之後兩種都列：兩種都改得動也刪得掉。

反過來也不能把資料表值函式併進 `Table`：那樣 `ALTER FUNCTION` 之後就列不出它們了。

`APPLY` 有自己的 `CompletionTarget` 而不是共用 `Function`，還有第二個好處：
`CompletionTarget.Function` 因此收斂成「`ALTER`／`DROP FUNCTION` 的那個名稱」，
提交時分得出「這裡要補引數」還是「這裡只要名稱」——見[函式引數](statement-values.md#提交函式時補上引數)。

## 系統物件只在兩個位置拉進來

`sys.objects`、`sys.dm_exec_requests`、`sp_executesql`、`sp_help` 這些原本一個都列不出來
——第一層查詢寫死 `is_ms_shipped = 0`，結構描述清單也明確排除了 `sys` 與
`INFORMATION_SCHEMA`。

它們現在有自己的查詢，而且**與第一層分開、只在被問到時才跑**：光是一個使用者資料庫
底下就有一兩千列，併進第一層等於每一次開啟查詢視窗都多付兩倍代價，換來的東西九成的
時間沒有人要。只有兩個位置會問：

- 使用者自己打出了 `sys.` 或 `INFORMATION_SCHEMA.`
- 游標在 `EXEC ` 之後——`sp_executesql`、`sp_help` 一律不加結構描述就呼叫

`ALTER PROCEDURE ` 不算，雖然它的目標同樣是預存程序：系統程序改不動，列出來只會讓
使用者選到一個改不了的東西，與內建函式不進 `ALTER FUNCTION` 是同一條理由。

這一份也刻意**不設有效期**：系統物件跟著 SQL Server 的版本走，不會在一次工作階段
中途變動，查一次就用到換連線為止。

`sys` 與 `INFORMATION_SCHEMA` 這兩個結構描述名稱則不必等中繼資料——它們在每一個
資料庫裡都存在，是產品事實而不是誰的 schema。少了這兩筆的話，使用者連「打 `sys`
再按 Tab」這條路都沒有。

`DROP` 家族與它們對稱：`DROP PROCEDURE`、`DROP FUNCTION`、`DROP VIEW` 之後同樣
只列那一類，但意圖是 `Reference`——那個位置要的只是一個名稱，把整份定義放進去
反而讓語句不合法。少寫哪一條都沒有徵兆，只是使用者在那個位置沒有清單。

觸發程序、序列與使用者自訂的資料表型別**只在自己的位置出現**，不進一般清單。
理由與全域變數同一條：`SELECT tr` 不該冒出觸發程序，而 `EXEC ` 之後選到一個觸發
程序一定執行失敗。觸發程序算模組（`OBJECT_DEFINITION` 拿得到定義），所以
`ALTER TRIGGER` 與 `ALTER PROCEDURE` 一樣直接展開完整定義。

這三種都不在 `sys.objects` 的原白名單裡，第一層查詢因此多收 `TR`、`TA`、`SO`，
並把 `sys.table_types` 另外 UNION 進來貼上 `TT` 標籤——與同義字的 `SN` 同一個做法。
資料表型別取的是 `type_table_object_id` 而不是 `user_type_id`：快取以 object_id 為鍵，
用型別自己的識別碼會與真的物件撞在一起，而那個 object_id 同時正好是它的欄位在
`sys.columns` 裡的鍵，於是欄位與滑鼠停留提示都不必另外接。

這一份不對資料庫送出任何查詢，因此與「列出資料庫物件」的設定無關；
也只在游標真的落在資料來源位置、而且沒有限定字時才掃——`FROM dbo.` 之後不該
出現沒有結構描述的名稱，而這條路徑在每一次按鍵上。

`ap` → `Tab` → 選取程序 → `Tab`，編輯器會直接放進該程序可執行的完整定義，
可以立刻修改並更新。定義開頭的 `CREATE` 或 `CREATE OR ALTER` 會改寫成 `ALTER`，
主體完全不動（主體裡的 `CREATE TABLE #tmp` 之類的語句不受影響），游標停在標頭的
物件名稱之後。`ALTER FUNCTION`、`ALTER VIEW`、`ALTER TRIGGER` 走的是同一份展開，
行為完全一致——那四種在 `SqlObjectKinds.IsModule` 裡是同一類，`OBJECT_DEFINITION`
都拿得到定義。

定義取不到只有兩個原因（物件是 `WITH ENCRYPTION` 建的，或這個登入沒有它的
`VIEW DEFINITION` 權限），這時維持只插入名稱，並在診斷紀錄裡寫明。
