# 查詢結果格線

回到 [索引](index.md)。

`SELECT` 跑完之後，在結果格線上按右鍵，選單底下會多出一段 **SqlAssist** 的命令，
把選取的資料變成可以直接用的 T-SQL。範圍只到「格線上已經有的資料」——這裡的每一個
命令都不會再連一次資料庫。

命令直接排在右鍵選單上，不收成子選單：用法是「選幾格、按右鍵、挑一個」，
多一層子選單就是每一次都多一次移動與展開。獨立成一個群組讓殼層在上下各畫一條
分隔線，第一項是一個永遠停用的 **SqlAssist** 標頭——分隔線畫得出「這是一組」，
畫不出「這一組是誰加的」，而同一個選單上其餘十幾項都是 SSMS 自己的。

## 命令

| 命令 | 產出 | 交到哪裡 |
|---|---|---|
| 建立 #temp 指令碼 | `DROP` 守門 ＋ `CREATE TABLE #SqlAssistRows` ＋ `INSERT` ＋ `SELECT` | 新查詢視窗（沿用同一個連線） |
| 複製成 IN 條件 | 一段接得上 `WHERE` 的述詞 | 剪貼簿 |
| 複製成 Markdown 表格 | 對齊過的 Markdown 表格 | 剪貼簿 |
| 欄位剖析 | 每一欄的 `NULL`／空字串／相異值數與範圍 | 一個可以複製成 TSV 的視窗 |
| 檢視這一格的完整內容 | 那一格的原文，加上型別與大小 | 一個可以選、可以捲的視窗 |
| 探測這個結果格線 | 格線內部狀態的報告 | 診斷紀錄檔（只在「詳細記錄」開啟時出現） |

三種產出的去處不同，因為用法不同：`#temp` 是完整的一段指令碼，開進新視窗按 F5
就能跑；`IN` 條件是要貼進手上那一句查詢的，開新視窗反而多一次搬運；欄位剖析的用途
是**看**不是貼，178 欄的摘要塞進查詢視窗變成 178 行註解，比原本捲格線還難讀。

欄位剖析在寬表上的價值遠高於窄表。真正想知道的往往只是「哪幾欄整欄是 `NULL`、
哪幾欄從頭到尾只有一個值」——那兩件事看資料看不出來，看摘要一眼就有。統計的對象
刻意就是眼前這一份結果，不是資料表的全貌；後者下一句 `GROUP BY` 比較快也比較準。

格線一列只有一行高，而它顯示的字數上限是 65535（`NumberOfCharsToShow`）——一段
`nvarchar(max)` 的 XML 在格線上只看得到開頭那幾十個字，而且沒有「後面還有」的提示。
「檢視這一格的完整內容」取的是選取範圍第一個區塊的左上角，也就是剛剛按右鍵那一格。
文字與 XML 給原文（要讀的是內容本身，多一層引號跳脫只會擋路），二進位給十六進位
並每 32 位元組換行，其他型別給 T-SQL 字面值（那時候的下一步多半是貼進一句 `WHERE`）。
長度單位跟著型別走：文字算字元、二進位算位元組——混成同一個數字的話，
「這一欄會不會被截斷」就答不出來了。

`NULL` 與空字串分開算：兩者在格線上都是不顯眼的一格，查問題時卻代表完全不同的事。
文字欄位另外報字元數範圍，那是找截斷的第一個線索——整欄都剛好 20 個字元的
`nvarchar(20)` 值得看一眼。最小與最大寫成 T-SQL 字面值而不是顯示字串，因為它們的
下一步幾乎一定是被貼進一句 `WHERE`。

## 為什麼沒有 Excel 匯出

SSMS 22 自己就有：「另存結果為…」的存檔對話方塊裡有 CSV、TSV、JSON、XML、
**Markdown** 與 **XLSX** 六種格式（`GridSaveFormats`）。重做一份只會多一個要跟著
SSMS 改版維護的東西。

Markdown 例外，因為內建那條路有一個補得起來的落差：它一律**寫成檔案**、一律
**整份結果**，看不到選取範圍。而真正要貼進工單、PR 或聊天室的時候，要的是剪貼簿
裡的那幾列。所以「複製成 Markdown 表格」做的只有那一半，XLSX 就不重做。

表格是給人讀的，所以值不帶引號也不帶 `N` 前綴，但日期的精確度仍然跟著型別走——
與儲存格視窗共用同一份判斷，否則同一個值在兩個地方長得不一樣，看起來像資料有問題。
真正的 `NULL` 寫成斜體的 `*NULL*`，一個內容剛好是 `NULL` 的字串就是那四個字：
兩者在渲染出來的表格上一個是斜體一個不是。豎線跳脫成 `\|`，換行換成 `<br>`——
不處理的話，前者會切出一欄不存在的欄，後者會把一列切成兩列。

## 為什麼沒有 UPDATE／DELETE

原本規劃過「照主索引鍵產生 `UPDATE`／`DELETE`」，查完之後放棄，理由是資料拿不到而
不是不想做。

結果格線的結構描述來自 `QEResultSet.GetSchemaRow`，而那份 schema table 是
`m_reader.GetSchemaTable()`——那個 reader 是用
`CommandBehavior.SequentialAccess`（連線啟用 Always Encrypted 時是 `Default`）
執行的，全組件沒有一處用 `KeyInfo`。少了 `KeyInfo`，schema table 的 `IsKey` 與
`BaseTableName` 都不會填。也就是說格線既不知道這些欄屬於哪一張資料表，
也不知道哪幾欄是鍵。

那就只剩下猜：從查詢文字裡挑一個 `FROM`，再假設選到的欄能唯一識別資料列。
`DELETE` 猜錯是救不回來的，而「識別出這幾列」這件事 `IN` 條件已經做到了——
把它貼進自己寫的 `DELETE` 只多一步，而那一步正是該由人確認的一步。

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
| Markdown 表格的長相 | `Metadata/ResultGrid/SqlMarkdownTableScript.cs` |
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
