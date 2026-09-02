# 查詢結果格線

回到 [索引](index.md)。

`SELECT` 跑完之後，在結果格線上按右鍵會多一層 **SqlAssist** 子選單，把選取的資料
變成可以直接用的 T-SQL。範圍只到「格線上已經有的資料」——這裡的每一個命令都不會
再連一次資料庫。

## 命令

| 命令 | 產出 | 交到哪裡 |
|---|---|---|
| 建立 #temp 指令碼 | `DROP` 守門 ＋ `CREATE TABLE #SqlAssistRows` ＋ `INSERT` ＋ `SELECT` | 新查詢視窗（沿用同一個連線） |
| 複製成 IN 條件 | 一段接得上 `WHERE` 的述詞 | 剪貼簿 |
| 探測這個結果格線 | 格線內部狀態的報告 | 診斷紀錄檔（只在「詳細記錄」開啟時出現） |

兩種產出的去處不同，因為用法不同：`#temp` 是完整的一段指令碼，開進新視窗按 F5
就能跑；`IN` 條件是要貼進手上那一句查詢的，開新視窗反而多一次搬運。

## 選取範圍怎麼算

- **沒有選取**：整份結果。
- **有選取**：所有被選到的列 × 所有被選到的欄，取聯集。

聯集是刻意的。選取範圍不保證是矩形——按住 Ctrl 點六格拿到的是六個 1×1 的區塊，
而 `INSERT` 與 `IN` 都需要矩形的資料。挖洞的那一份要嘛補 `NULL`（值就錯了），
要嘛拆成好幾段（更難用）。產出的第一行一律寫明實際的形狀，範圍被撐開時看得出來。

一次最多 200,000 格（`ResultGridSelectionPlan.MaxCells`）。門檻用格數而不是列數：
一個 178 欄的查詢，1000 列就是 17.8 萬格，而列數的門檻在寬表與窄表上差太多。

## 值怎麼變成字面值

`Metadata/ResultGrid/SqlValueLiteral.cs` 是**唯一出處**，`#temp`、`IN` 條件與之後的
命令全部經過它。三條規則各自對應一種「跑得動而答案是錯的」：

- **日期一律 ISO 8601。** `'2024-03-04'` 插進 `datetime` 會隨連線的 `DATEFORMAT`
  與語言改變解讀。分隔的 `yyyy-MM-ddTHH:mm:ss` 不受影響。精確度跟著欄位型別走，
  不一律取最長——`date` 欄寫成 `.0000000` 之後，`IN` 條件就比對不到任何一列。
- **型別不確定時文字加 `N` 前綴。** 多一個 `N` 只是一次隱含轉換；少一個 `N`
  是把非拉丁字元換成問號，沒有錯誤訊息。
- **`NULL` 不用等號比。** `x IN (NULL)` 與 `x = NULL` 都恆為 UNKNOWN，於是使用者
  明明選了那一列，條件卻永遠比不到它。`NULL` 一律改寫成 `IS NULL`。

## 什麼時候整段拒絕

輸出換成一整段註解，寫明缺什麼、怎麼辦——與 `SqlObjectStructure` 缺定義時同一份
判斷。三種情形：

- 某一欄的值轉不成字面值（空間型別、`hierarchyid`、`sql_variant`）。
- 某一欄問不出伺服器型別，`CREATE TABLE` 沒有東西可寫。
- 選取範圍超過格數上限。

不做部分輸出：少一欄的 `INSERT` 執行得動，而拿它 debug 的人不會發現資料少了一塊。

## 為什麼不走剪貼簿

SSMS 自己的「複製」給的是 TSV，而那份文字裡資料庫的 `NULL` 和一個內容剛好是
`NULL` 這四個字的字串長得一模一樣，欄位型別也整個消失。實測確認過：字面 `'NULL'`
字串取回來是 `SqlString`、`IsCellDataNull` 是 `False`；真正的 `NULL` 取回來是
`null`、`IsCellDataNull` 是 `True`；**兩者字串化之後都是長度 4 的 `NULL`**。

所以資料一律從 `GridControl.GridStorage` 取，而且**先問 `IsCellDataNull` 再取值**。

## 兩套欄索引

同一個儲存體上有兩種欄索引，換算只在 `Ssms22/ResultGrid/SsmsResultGrid.cs` 做一次：

| 方法 | 索引基準 |
|---|---|
| `GetFieldType`、`GetServerDataTypeName`、`GetSchemaRow` | 資料欄，0 起算 |
| `GetCellData`、`IsCellDataNull`、`GetCellDataAsString` | 格線欄，第 0 欄是列號欄 |

搞錯的症狀不是例外，是整份資料錯開一欄而每一格都還「有值」。第一版探測假設兩邊
一致，第 0 欄直接 `ArgumentOutOfRangeException`——那次是撞到邊界才炸的。

使用者拖動過欄位順序時，選取範圍給的欄座標是畫面上的位置，與儲存體的原始順序
對不上。這種情形**整段拒絕**並請使用者還原順序：`GetOriginalColumnIndex` 的方向
沒有文件，猜錯的症狀與不換算完全一樣。

## 效能

按下命令到看見結果之間，最貴的是取值那一段：178 欄 × 1000 列就是 17.8 萬次呼叫。

- 反射方法一律先用運算式樹編成強型別委派再呼叫，並依「型別＋方法名」快取
  （`Ssms22/ResultGrid/GridReflection.cs`）。`MethodInfo.Invoke` 每次都要配置
  引數陣列並裝箱，而那正是這裡最貴的一段。
- `IsCellDataNull` 另外綁一個回傳 `bool` 的委派，不裝箱。
- 字面值只轉一次，同一份結果餵給長度估算與字串組裝
  （`Metadata/ResultGrid/ResultGridLiterals.cs`）。
- `StringBuilder` 依估算的長度預先配置。

格線的資料本來就在記憶體裡（`StoredAllData`），所以這些命令都不會重新查詢資料庫。

## 進去改的位置

| 想做的事 | 檔案 |
|---|---|
| 值轉成字面值的規則 | `Metadata/ResultGrid/SqlValueLiteral.cs` |
| `#temp` 指令碼的長相 | `Metadata/ResultGrid/SqlTempTableScript.cs` |
| `IN` 條件的長相 | `Metadata/ResultGrid/SqlInPredicateScript.cs` |
| 選取範圍怎麼換算、格數上限 | `Metadata/ResultGrid/ResultGridSelectionPlan.cs` |
| 從格線取資料 | `Ssms22/ResultGrid/SsmsResultGrid.cs` |
| 產出交到哪裡、失敗怎麼說 | `Ssms22/ResultGrid/ResultGridActions.cs` |
| 選單項目 | `Ssms22/Menus.vsct`（改完要加 `ProvideMenuResource` 版號並重新安裝） |

新增一個結果格線命令時，前兩步（找格線、讀資料）走 `ResultGridActions.Prepare`，
值的轉換走 `SqlValueLiteral`。另寫一份的症狀是其中一份改了另一份沒改，
而兩邊產出的 SQL 都執行得動，只有一邊的值不對。
