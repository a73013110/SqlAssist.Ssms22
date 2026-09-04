# 結果格線的值與輸出

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

## 長度與精確度

結果格線回報的型別名稱**不帶括號**：`GetServerDataTypeName` 給的是 `varchar`，
不是 `varchar(20)`。而 T-SQL 對省略的長度另有預設值，兩者湊起來就是那一句
「字串或二進位資料會被截斷」——`CREATE TABLE` 裡的 `varchar` 就是 `varchar(1)`。

一句錯誤訊息還算好的。`decimal` 省略精確度是 `decimal(18,0)`，小數點後面整段被
四捨五入掉，沒有錯誤也沒有警告，而這個功能的用途正是把資料原封不動搬過來。

長度與精確度另外從結構描述列（`GetSchemaRow` 的 `ColumnSize`、`NumericPrecision`、
`NumericScale`）問。規則只有一條：**寧可放寬，不可猜窄**（`SqlTempTableColumnType.cs`）。

| 情形 | 寫出來的型別 |
|---|---|
| 問得到長度 | 照抄，`varchar(20)` |
| 問不到長度 | 同族裡裝得下任何值的那一個：`varchar(max)`、`nvarchar(max)`、`varbinary(max)` |
| 定長型別問不到長度 | 一併換成變長的——沒有 `char(max)` 這種東西 |
| 長度超過型別上限（`nvarchar(max)` 回報 1073741823） | `(max)`，那本來就是它的意思 |
| 問不到 `decimal` 精確度 | 總位數取滿 38，小數位數取實際出現過的最多那一個 |
| 省略時預設就是最大值（`datetime2`、`time`、`float`） | 不加括號 |

不照觀察到的最長那一列開長度，理由與整段都寫成允許 `NULL` 相同：格線知道的是
「這一次查到的資料」，不是欄位的定義；照資料收緊，使用者改資料重跑時就會被截斷。
`decimal` 是唯一的例外，因為它沒有 `(max)` 可以退——而它安全的理由是同一欄的值
來自同一個 `decimal(p, s)`，取滿 38 位再配上觀察到的最大小數位數一定裝得下。

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
| 欄位長度與精確度怎麼補 | `Metadata/ResultGrid/SqlTempTableColumnType.cs` |
| `IN` 條件的長相 | `Metadata/ResultGrid/SqlInPredicateScript.cs` |
| Markdown 表格的長相 | `Metadata/ResultGrid/SqlMarkdownTableScript.cs` |
| JSON 的長相與型別對應 | `Metadata/ResultGrid/SqlJsonArrayScript.cs` |
| 欄位剖析算什麼 | `Metadata/ResultGrid/ResultGridProfile.cs` |
| 儲存格內容怎麼呈現、值的顯示文字 | `Metadata/ResultGrid/ResultGridCellText.cs` |
| 兩個視窗的長相 | `Ssms22/ResultGrid/ResultGridProfileWindow.cs`、`ResultGridCellWindow.cs`（外觀一律走 `UI/SqlAssistChrome.cs`） |
| 選取範圍怎麼換算、格數上限 | `Metadata/ResultGrid/ResultGridSelectionPlan.cs` |
| 從格線取資料 | `Ssms22/ResultGrid/SsmsResultGrid.cs` |
| 產出交到哪裡、失敗怎麼說 | `Ssms22/ResultGrid/ResultGridActions.cs` |
| 選單項目 | `Ssms22/Menus.vsct`（改完要加 `ProvideMenuResource` 版號並重新安裝） |

新增一個結果格線命令時，前兩步（找格線、讀資料）走 `ResultGridActions.Prepare`，
值的轉換走 `SqlValueLiteral`。另寫一份的症狀是其中一份改了另一份沒改，
而兩邊產出的 SQL 都執行得動，只有一邊的值不對。
