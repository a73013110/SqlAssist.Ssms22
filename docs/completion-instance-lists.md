# 執行個體名單

本頁處理名單只在伺服器上的三個位置：`COLLATE` 的定序、`SET LANGUAGE`／`DEFAULT_LANGUAGE =` 的語言、
`AT TIME ZONE` 的時區；名單在本機的封閉位置（日期部分、提示）見[上下文](completion-context.md)。

## 同一種名單

剖析器在這三格什麼名稱都收，片語給不出字（見[子句片語](completion-phrases.md)）。名單只在伺服器上，
而且隨版本增加（定序在 SQL Server 2019 之後超過 5,500 筆）：寫死的那一份會在下一版開始漏名稱，
漏掉哪一個使用者看不出來。

三份處境相同，所以只有一條管線。Core 的 `SqlInstanceList` 描述每一份名單的差異——前導字、值的寫法、
文法上的字——位置、指令碼已用值、排名分級與插入文字都由它推出；Metadata 的 `SqlInstanceListQuery`
只放查詢與通知標題。新增一份名單是一個描述子、一組查詢與一個 `CompletionTarget`。

| 名單 | 前導字 | 名單來源 | 在用的值 | 寫法 |
|---|---|---|---|---|
| 定序 | `COLLATE` | `sys.fn_helpcollations()` | 資料庫定序（`DATABASEPROPERTYEX`） | 原樣 |
| 語言 | 一句開頭的 `SET LANGUAGE`、`DEFAULT_LANGUAGE =` | `sys.syslanguages`，別名寫在列尾 | `@@LANGUAGE` | 識別字 |
| 時區 | `AT TIME ZONE` | `sys.time_zone_info`，UTC 位移寫在列尾 | `CURRENT_TIMEZONE_ID()` | 字串常值 |

## 位置

只看游標前緊接著的前導字：三種 `COLLATE`（運算式之後、資料行定義、`CREATE`／`ALTER DATABASE`）
前面長得都不一樣，後面要的完全相同。`SET` 要是一句的開頭（`SqlStatementBoundaries.IsStatementHead`），
`UPDATE r SET Language ` 的 `Language` 是資料行。

判定成立時目標收斂成 `Collation`、`Language` 或 `TimeZone`，清單封閉，打完空白就開；不佔
`SqlKeywordPosition` 的位元。`DEFAULT_LANGUAGE =` 一組前導字涵蓋 `CREATE`／`ALTER` 的 `LOGIN`、`USER`
與 `DATABASE` 選項。刻意不涵蓋的：

- `sp_configure 'default language'`：值是 `langid` 數字，不是名稱。
- `sp_defaultlanguage` 這類程序引數：位置由 `EXEC` 的參數規則決定，不是前導字。
- `AT TIME ZONE` 之後的資料行：清單只列名單；`@` 開頭照常走變數。
- 游標在字串常值裡（`AT TIME ZONE 'Tai`）：與其他字串同一條規則，不補；清單在打完空白時就開了。

## 指令碼已用值

值就是寫在名單位置上的詞元，所以掃描用同一條位置規則：對每個詞元問「它前面是不是這份名單的
前導字」，不另寫一份掃描。只收形狀對的值——定序只收不加括號的識別字，時區只收字串
（`AT TIME ZONE r.Zone` 是資料行），語言兩種都收；文法上的字（`DATABASE_DEFAULT`、`CATALOG_DEFAULT`）
不重複收。只在游標落在名單位置時才掃。

## 快取與降級

名單的快取鍵是**伺服器**加名單（`SqlServerInstanceListCache`），不是伺服器加資料庫：名單回答的是
這個執行個體支援什麼。跟著每一份目錄各存一次的話，每打一個跨資料庫的限定字就多五千多個定序字串，
並對同一台伺服器多送一輪查詢。在用的值跟著目錄。與系統物件同一條理由，不設有效期，也不掛在目錄的
`Invalidate` 上——那一次清的是某一個資料庫，而名單是別的目錄也在用的。

連結伺服器的目錄一律回空：指令碼跑在**本機**那條連線上，列出對面那台的名稱可能在這裡根本不存在。

`DbException` 照[相容與失敗](metadata-compatibility.md)降級成空名單，失敗不進快取。那個位置不會因此
空掉：文法上的字與指令碼已用值都不必查詢。這一版沒有的東西不算失敗，照同一頁「這一版沒有」那一條寫。

`@@LANGUAGE` 問的是中繼資料自己開的連線，也就是登入的預設語言；查詢視窗裡 `SET LANGUAGE` 過的值
由指令碼已用值補上。

名單受「列出資料庫物件與欄位」管：關掉它的人要的是「不要連線」，不必問伺服器的兩份仍然列出，
與 CTE、暫存資料表在那個設定下的處置一致，因此沒有另設開關。

## 排名

值分成兩個 `SuggestionKind`：`InstanceListValueInUse`（在用的值與指令碼已用值）與 `InstanceListValue`
（其餘）。理由與 `ScriptDataSource` 對 `Table` 相同：東西是同一種，排名必須不同。定序名稱只差
`_CI_AS`、`_CS_AS` 這種尾巴，模糊比對撈回來的順序沒有意義，名稱長度還會把短的推到前面；類別加成差
一級就壓得過長度懲罰與最近使用，見[補全](completion.md)。同一個名稱只列一次，先列的那一級留下。

三份名單共用這兩類，圖示由建議項帶著的 `SqlInstanceList`（`Tag`）分辨。不參與分類篩選：清單裡
只有這一種，給它一顆篩選鈕按了畫面也不會變。

## 插入文字

寫法由名單決定（`SqlInsertionText.InstanceListValue`），不歸「插入物件時加上方括號」管，這些不是物件名稱：

- 定序原樣：`COLLATE [Latin1_General_CI_AS]` 是語法錯誤。
- 語言寫識別字，形狀不合或是保留字才包（`[Português (Brasil)]`）。`SET LANGUAGE` 也收 `N'…'`，
  但 `DEFAULT_LANGUAGE =` 只收識別字，兩處都寫得進去的只有這一種。
- 時區寫字串常值，含非 ASCII 字元才加 `N`：`AT TIME ZONE UTC` 剖析得過，卻是資料行參考。
