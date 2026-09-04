# F12 的物件指令碼

本頁只處理不同物件要產生哪一種定義；開窗與執行緒見[移至定義](go-to-definition.md)。

## 產生出來的是什麼

| 物件種類 | 內容 |
|---|---|
| 預存程序、函式、觸發程序、檢視 | `OBJECT_DEFINITION` 的原文，開頭的 `CREATE` 改寫成 `ALTER` |
| 資料表 | 重建的 `CREATE TABLE`，後面接索引與外來鍵 |
| 資料表型別 | 重建的 `CREATE TYPE ... AS TABLE`，主索引鍵寫成不具名的內嵌條件約束 |
| 取不到定義的模組 | 整段註解，寫明兩個可能的原因與查得到的欄位、參數 |
| 取不到欄位的資料表、資料表型別 | 整段註解，寫明兩個可能的原因 |
| 同義字、序列 | `CREATE SYNONYM`、`CREATE SEQUENCE`（維持 CREATE，這兩種沒有整體的 ALTER 寫法） |
| 認不出來的種類 | 整段註解，寫明為什麼組不出可執行的指令碼 |

前五列與浮動預覽的**指令碼**分頁是同一份（[structure-preview.md](structure-preview.md)），
差別只在這裡多包了批次樣板並把模組改成 `ALTER`。

開頭那兩個 `SET` 不是裝飾。`ALTER PROCEDURE` 必須是批次裡的第一個敘述，所以它們
後面一定要有 `GO` 才分得開；而計算欄位、篩選索引與索引檢視對這兩個選項的值有要求，
少了它們的 `CREATE TABLE` 在某些連線設定下會直接失敗。SSMS 自己的「編寫指令碼為」
也是照這三行開頭的。

### 為什麼是 ALTER 而不是 CREATE

F12 之後接著要做的事幾乎都是「改一下再執行」。給 `CREATE` 的話每一次都要自己把
第一個字改掉，而那正是提交建議時 `ap` 展開成完整 `ALTER` 定義已經在做的事
（[整句展開](statement-expansion.md)）——兩條路徑對同一個模組給出不同的開頭關鍵字，
只會讓人以為其中一條壞了。

資料表與資料表型別**不**改寫：`ALTER TABLE` 沒有整體寫法，`ALTER TYPE` 不存在。
同義字也沒有整體 `ALTER`；`ALTER SEQUENCE` 則改不了型別。硬改只會讓指令碼執行失敗。

### 為什麼還留著「整段註解」這一支

只剩 `SqlObjectKinds.FromSysObjectType` 認不出來的種類會走到那裡。這一支不能拿掉：
沒見過的型別代碼一律對應到 `Unknown`，而 SQL Server 的物件型別只會愈來愈多，
硬湊一份指令碼出來就是指令碼在說謊。

同義字、序列與資料表型別曾經都在這一列。前兩者的定義其實就是目錄檢視上的那幾個
欄位（`sys.synonyms.base_object_name`、`sys.sequences` 的界限與快取），
現在由 `Metadata/Formatting/SqlCatalogScript` 組回 `CREATE`，見
[metadata.md](metadata.md)。資料表型別則有欄位，不擋掉會被寫成 `CREATE TABLE`，
照著執行會多出一張同名的資料表——而它需要的 `CREATE TYPE ... AS TABLE` 查得到的
資料就組得出來，所以現在直接組，三種都不必再繞。

「哪一類寫得出可以執行的指令碼」由 `SqlObjectKinds.HasExecutableScript` 一份說了算，
這條路徑與浮動預覽的指令碼分頁共用。各留一份判斷的症狀已經發生過一次：同一個
資料表型別，F12 給註解，預覽卻給 `CREATE TABLE`。

種類過得了關、這一次的資料卻不齊時（模組沒有定義、資料表沒有欄位），
註解是 `SqlObjectStructure` 那一端換的，這裡原樣帶出來——那一份寫的是缺什麼與
為什麼，與上面「這一類物件本來就組不出來」是兩件事。判斷同樣只有一份，
見 [metadata.md](metadata.md)。
