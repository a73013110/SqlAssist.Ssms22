# COLLATE 之後的定序

`COLLATE ` 之後文法上只接得了定序名稱、`DATABASE_DEFAULT`（運算式裡）與
`CATALOG_DEFAULT`（全文檢索）。三種 `COLLATE` 的位置——運算式之後、資料行定義、
`CREATE`／`ALTER DATABASE`——前面長得都不一樣，後面要的東西卻完全相同，
所以 `SqlArgumentPosition` 只認 `COLLATE` 這一個字，不分位置。

## 清單不做成內建目錄

名單來自 `sys.fn_helpcollations()`。SQL Server 2019 之後有 5,500 筆以上且隨版本
增加，寫死的那一份會在下一版開始漏掉名稱，而漏掉哪一個使用者完全看不出來。

名單的快取鍵是**伺服器**，不是伺服器加資料庫（`SqlServerCollationCache`）：
`sys.fn_helpcollations()` 回答的是這個執行個體支援什麼，與連到哪一個資料庫無關。
跟著每一份 `SqlMetadataCatalog` 各存一次的話，使用者每打出一個跨資料庫的限定字
就多五千多個字串，並對同一台伺服器多送一輪查詢。

與系統物件同一條理由，刻意不設有效期，也刻意不掛在目錄的 `Invalidate` 上——
那一次重新整理清的是某一個資料庫，而這一份是別的目錄也在用的。

連結伺服器的目錄一律回傳空的。定序屬於執行個體，而使用者編輯的這份指令碼跑在
**本機**那條連線上；列出對面那台的名單，選中的名稱可能在這裡根本不存在。

## 查不到的時候

`DbException` 照[相容與失敗](metadata-compatibility.md)降級成空名單，失敗不進快取。
那個位置不會因此空掉：`DATABASE_DEFAULT`、`CATALOG_DEFAULT`（`SqlCollationCatalog`）
與這份指令碼已經寫過的定序（`SqlScriptCollationSuggestions`）都不必送出查詢。

同一條理由決定了設定：這份清單**受**「列出資料庫物件與欄位」管。關掉它的人要的
是「不要連線」，而名單一定要送查詢；不必問伺服器的那兩份仍然列出來，與 CTE、
暫存資料表在那個設定下的處置一致。因此沒有為定序新增設定項。

## 排名

5,500 個名稱長得幾乎一樣，只差 `_CI_AS`、`_CS_AS` 這種尾巴，模糊比對撈回來的順序
沒有意義，而名稱長度反而會把短的推到前面。所以分成兩個 `SuggestionKind`：

- `CollationInUse`：目前資料庫的定序（`DATABASEPROPERTYEX`）與這份指令碼裡出現過
  的定序。這是使用者幾乎一定要的那一個——加上 `COLLATE` 通常正是要把某一邊
  **對齊**到它。
- `Collation`：其餘的名單。

分兩類而不是另寫一套排名，理由與 `ScriptDataSource` 對 `Table` 完全相同：東西是
同一種，排名必須不同。類別加成差一級就壓得過長度懲罰與最近使用，見
[補全](completion.md)。同一個名稱只列一次，重複的由中繼資料那一側濾掉。

分類歸「其他」，與資料庫、型別那幾類同一條取捨：`COLLATE ` 之後清單裡只有定序，
給它一顆篩選鈕按了畫面也不會變。
