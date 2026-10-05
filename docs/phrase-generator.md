# 子句片語的產生器

本頁處理子句片語的探測：尾巴的寫法、展開、證據與前一格；選項清單見[清單片語](phrase-lists.md)，執行期的比對與過濾見[子句片語](completion-phrases.md)。

## 尾巴的寫法

`tools/SqlAssist.KeywordGenerator` 的第四階段。手寫的只有 `Data/*Phrases.cs` 的尾巴，探測前一次驗完：

| 寫法 | 意思 |
|---|---|
| `{name}` | 一個名稱單位，含點號；保留字（`ALTER DATABASE CURRENT`）與變數（`BACKUP DATABASE @db`）也算 |
| `{value}` | 一個運算式：常值、變數、名稱、函式呼叫或一整組括號，以算術運算子串起來（`SqlOperand`） |
| `=`、`:` | 選項的等號（`ALGORITHM =`）、`JSON_OBJECT` 鍵與值之間的冒號 |
| `,` | 逗號，分隔同一句重複的一段（`ADD EVENT a.b, ADD`） |
| `()` | 一整組括號 |
| `(` | 括號裡是子句（`WAITFOR (`）：只認緊接的左括號 |
| `(*` | 還沒關上的左括號清單，游標在 `(` 或逗號之後；後面再寫的項是清單某一項的開頭（`WITH (* TYPE =`），固定的第一項由 `Items` 墊 |
| `,*` | 標頭開的逗號清單，游標在逗號之後；寫在中段是標頭之後零到多項（`ADD FILE ,* (*`：第幾組括號都一樣） |
| `...` | 動詞之後、下一個字面字之前的其餘標頭；第一個詞元不能是關鍵字（`EXECUTE AS … WITH` 是別的敘述） |
| （空） | 沒有尾巴，只認位置：「這個位置接得了這些字」；必須寫 `After` |

候選字是關鍵字清單加上 ScriptDom 內部 `CodeGenerationSupporter` 的字串常數，普通名稱過不了而它過得了才算。
比的除了整段，還有「撐過字本身」：`ROWS BETWEEN UNBOUNDED` 要再接 `PRECEDING` 才完整，只比整段的話
它與普通名稱一起被拒。剖析器也有讀完才回頭驗的地方（`DECRYPTION BY CERTIFICATE KEY x` 在 `CERTIFICATE` 報錯）：
讓前面的字被拒過的字，要有續尾把整句寫完才算。
普通名稱在任何續尾都過不了的片語是**封閉**的；名稱後面還要再寫一段的
（`UPDATE t SET`）也會判成封閉，由 `Closed = false` 宣告不封閉。
換 `@ReaderId` 過得了或只收變數的（`BEGIN DIALOG @h`）另記 `TakesVariable`，收名稱不收值的另記 `TakesName`（[目錄物件](completion-catalog-names.md)拿它認名稱格）。
選項的字在剖析器眼中也是名稱，名稱格照樣封閉；證據補完仍一個字都沒有的才改成不封閉（`CREATE CERTIFICATE c AUTHORIZATION ` 還要 `FROM`）。

探測文字本身已是完整語句時（`CREATE INDEX i ON t (a) `），接得上的字也含下一句的開頭；
產生器扣掉在 `SELECT 1; ` 探到的那一份，被誤扣的（`WITH` 也是 CTE 的開頭）由更長的片語或 `Values` 補回。
這種片語帶 `EndsStatement`，游標換了行就只算可能，見[片語](completion-phrases.md#執行期)。

- `Expand`：每個接得上的字接在後面成為新片語。語句標頭寫完了照樣往下（`CREATE MASTER KEY` 之後的 `ENCRYPTION`），
  扣掉下一句的開頭沒有字就不立；子句裡的不往下，由位置分析說（立了會藏掉索引篩選 `IS NOT NULL` 之後的 `WITH`）。
  普通名稱放在同一格也完整時，完整的可能只是名稱讀法（`OPEN SYMMETRIC` 也是資料指標）：另外扣掉名稱之後
  接得上的字（`FETCH NEXT ` 之後不列 `INTO`）。
  值、名稱與選項的等號各是一步、不算一層（`FETCH ABSOLUTE 1 FROM`）；接得了值的格子是運算式，
  不走名稱與等號。等號之後的值不逐一展開，以第一個字與數值各往下一次（`SIZE = 5 MB`）。同一格只有層數比上次多才再展開，已由位置片語說了的不立。
  宣告的 `Endings` 跟著往下：展開出來的每一格是同一條宣告（`MASKED` 要寫完 `WITH (FUNCTION = …)` 才驗）。
- 探測代入：`{value}` 用剖析器收的數值或字串，都不收的照 `{name}`（`CONTAINSTABLE (` 的資料行）；`{name}` 用普通名稱，只收變數的用變數，其餘收不了的格子與等號之後用那一格列得出的
  第一個字（手寫的也算）：`ALGORITHM =` 什麼名稱都先收，整句寫完才驗。剖析器要看到下一個字才收名稱的
  （`ALTER SERVER AUDIT a WITH`）接上片語的下一項判，只收兩段的（擴充事件）代入 `t.t`。
- `Kinds`：`CREATE`、`ALTER`、`DROP` 之後是物件種類，展開到名稱為止，名稱之後只列一層（`CREATE TABLE t ` 之後的 `AS`）：
  否則 `CREATE PROCEDURE p AS` 之後是整份語句開頭。更深的標頭各自宣告，已是位置的（`CREATE SEQUENCE t `）由位置片語說。
  `CREATE` 寫到名稱的種類（`SYMMETRIC KEY`、`UNIQUE CLUSTERED INDEX`、`OR ALTER PROCEDURE`）輸出成 `CreatedKinds`，
  位置分析拿它判新名字，不手寫名單；名稱寫得成兩段式（`CREATE INDEX s.i` 不行）另記 `SchemaQualified`。
  名稱之後接得上 `AUTHORIZATION` 的另立擁有者那一格（`CREATE SCHEMA s AUTHORIZATION`）。
- `Values`：剖析器把值當名稱看、分不出來時才手寫（`SET DATEFORMAT` 的 `dmy`、資料庫加密金鑰的演算法）。
  每個值仍要接得上一組續尾（含 `Endings`）或開得了一組清單，否則中止產生；`Closed` 由人宣告那一格只有這幾個值。
  手寫的值也往下展開。
- `Template`：`After` 位置的第幾個樣板（`IS` 要 `WHERE a `，不是 `WHERE a = 1 `）。換樣板探的仍是同一格：
  證據補進那一格的片語、字取聯集（計算資料行才接的 `PERSISTED` 補進型別之後那一格）。

## 片語裡的每一個字

寫得出 `CREATE OR ALTER`，`CREATE ` 之後就要有 `OR`；逐字探測問不出
（`CREATE OR` 接任何續尾都在 `CREATE` 就報錯），所以拿整段剖析得過的片語當證據，
把每一個字補進它前面那段。權限 `ON` 之後的多字類別（`SEARCH PROPERTY LIST::`）也是：
`Classes` 拿 `CreatedKinds` 的多字種類代入，剖析得過的就是證據。那段還不是片語就另立一個，條件是尾巴認得出來——
以字面字或等號結尾，`Lead` 片語至少兩項。以名稱或值結尾的一段之後什麼都可能接，單獨的 `ON` 到處比對得上。
例外：帶位置的片語已由位置釘住，以名稱或值結尾也立（`ALGORITHM = AES_256` 之後的 `ENCRYPTION` 剖析器當名稱讀）；
單獨一個不是關鍵字的立成不封閉。以 `...`、括號或清單結尾的不立。
`Lead` 片語的第一個字不是關鍵字、別的來源也列不出時，收進 `None` 的附加片語。

語句說明也是證據，名稱與別名只補進已有的片語（另立會封閉 `BEGIN ` 其餘的字）。`DBCC ` 之後
探不出字，名單只有說明；命令括號裡的字（`NORESEED`）同理：說明裡以 `DBCC 命令` 開頭的每一格（預覽的「命令」表一列一個，
別名少了寫法就中止產生）第一組括號裡的大寫字，立成不封閉的 `DBCC 命令 (*`。

第一個字前面那段是位置，不是片語。那個位置有只認位置的片語就補進去；別的來源都列不出時，另立**附加片語**：只認位置，
比對永遠是「可能」，只加字、不藏字——確定的比對會讓 `SELECT a ` 之後只剩 `AT`。
別的來源：關鍵字目錄、前面那段的片語、名稱（第一個字換成普通名稱
也剖析得過；一項寫完才驗的 `BEGIN ATOMIC` 要過 `Endings`）、內建函式（`functions.json`；引數自有文法的換成名稱不過），
以及判不出位置時一併比對得上的其他附加片語（`None` 的 `AT`）。

## 唯一的接續併成一項

後面只接得了一個字的字與那個字併成一項：`ASYMMETRIC KEY`、`ENCRYPTION BY PASSWORD`、`OR ALTER`，選一次寫完。
條件是那個字寫到這裡還沒完整、封閉、接不了名稱、值、括號或 `::`——`OPEN SYMMETRIC` 也是完整的資料指標語句，不併；
括號清單寫完一項也算完整（`ENCRYPTION = REQUIRED` 不併 `ALGORITHM`）。
中間每一段的片語照舊；片語接不接得上一個字認的是一項的第一個字。

## 前一格

同一條尾巴在不同位置意思不同（一句開頭的 `SET` 接工作階段選項，`UPDATE t SET` 接資料行），
所以每個片語交代第一個字前面那一格，二選一：

- `After`：第三階段樣板表的位置，探測用該位置的**第一個**樣板，執行期由同一個位置分析回驗；
  兩個都不寫就是 `StatementStart`。
- `Lead`：前一格判不出位置、尾巴本身認得出意思（`WITH EXECUTE AS`）時探測要墊的文字，執行期不看前一格。
  判得出來的一律寫 `After`。

展開出來寫完一個選項的片語回到位置本身，字併進位置片語的：位置樣板接上哪一條續尾就完整，它接上同一條也完整
（型別之後的 `NULL`、`SPARSE`，序列的 `START WITH 1`）；寫到一半的（`NOT`、`NO`）照舊。片語只拿一個樣板探，
位置片語的字由各樣板的證據補齊，照自己探到的列的話 `ALTER COLUMN a int NOT NULL ` 之後沒有 `WITH`。

前一格判不出位置的（選取清單以外的 `NEXT VALUE`、預設值條件約束）寫更長的 `Lead` 尾巴，比對取項數多的。
從判得出的那一格寫得到就從那裡寫：視窗框架中段（`AND` 之後）判不出位置，片語從 `ROWS` 寫起，端點寫成一份表
展開成每一格（`WindowPhrases`）；函式引數裡判不出位置，`JSON_OBJECT (* {value} : {value} NULL ON NULL` 從呼叫寫起。
同一條尾巴在幾種敘述接的字不同（兩種稽核規格的 `ADD (`）時，`AlsoLeads` 各墊一次，字取聯集；
證據立起的那一段只拿 `Lead` 探，墊接得最多的敘述（條件約束墊資料行層級，`NONCLUSTERED` 之後還接 `NOT NULL`）。
