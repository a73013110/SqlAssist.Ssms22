# 欄位建議

別名指向哪些來源、指令碼自己宣告的資料表怎麼讀、範圍在哪裡切開，以及詞元結束後
清單要怎麼重開。欄位提交後展開成整句的規則見
[completion-commit-expansion.md](completion-commit-expansion.md)，清單怎麼排名與
觸發見 [completion.md](completion.md)。

輸入 `別名.` 或 `資料表名稱.` 時列出該資料來源的欄位，並顯示型別、NULL 與 PK。

別名解析需要看得到游標**後方**的文字：

```sql
SELECT u.| FROM dbo.Lib_Reader u
```

FROM 子句在游標之後，只看前文永遠解析不出 `u`——而編輯既有查詢正是最常
遇到這種情形的時候。因此上下文分析改用完整文字加游標位置的多載。

## 別名指向哪些欄位

「這個別名給得出哪些欄位」與展開 `SELECT *` 是同一個問題，答案只有一份：
`Core/Parsing/SqlColumnSourceResolver`。資料表與檢視交給中繼資料層，
子查詢與 CTE 的輸出欄位直接讀它們的選取清單，內層自己又是 `*` 時遞迴下去。
細節見 [wildcard-expansion.md](wildcard-expansion.md#欄位從哪裡來)。

各寫一份的症狀曾經就在眼前：同一段 SQL 的 `a.*` 按 Tab 展得開，`a.` 卻一個建議
都沒有——只有萬用字元那一份會往子查詢裡看，欄位建議那一份遇到衍生資料表就放棄，
遇到 CTE 名稱則當成資料庫裡的資料表去查，而那張資料表並不存在。

```sql
SELECT a.| FROM (SELECT c.PUBL_CODE, c.SHELF_LOCATION_CODE FROM dbo.PUBLISHER c) a
;WITH c AS (SELECT Id FROM dbo.Item) SELECT x.| FROM c x
```

暫存資料表與資料表變數走的是同一條路：它們的欄位中繼資料一列都查不到——
資料表變數不是 `sys.objects` 裡的物件，暫存資料表在 tempdb 裡，而擴充只查目前連線的
那一個資料庫——但那些欄位就寫在使用者眼前的 `CREATE TABLE #Loan (…)` 與
`DECLARE @rows TABLE (…)` 括號裡，讀得出來，見[指令碼宣告的資料表](#指令碼宣告的資料表)。

只有讀不出宣告的時候才放棄（例如 `SELECT … INTO #Loan` 建立的暫存資料表，
那裡沒有資料行定義）。放棄時維持原本的結構描述解讀，讓使用者至少還看得到物件清單。

子查詢與 CTE 讀出來的欄位沒有型別、NULL 與 PK——那些要追到最內層的資料表，
而中間任何一段運算式都會讓答案不成立。說明欄因此只寫「查詢結果」。

## 資料表值函式的別名

```sql
SELECT f.| FROM dbo.fn_LoansByReader(0) f
SELECT * FROM dbo.Loan l CROSS APPLY dbo.fn_LoansByReader(l.CopyNo) f
```

`f` 與資料表的別名走同一條路：範圍分析把引數清單整段跳過——引數裡的逗號不是來源
清單的逗號，巢狀的括號也要一次跳完——攤平出來的仍然是一個中繼資料來源，
欄位由第二層的 `sys.columns` 給，見 [metadata.md](metadata.md) 的
「資料行查得到，不代表它是一張資料表」。`SELECT *` 展開同理。

只有提交後的展開閘門要另外問一句：`INSERT INTO dbo.fn_LoansByReader` 剖析不過，
所以那裡問的是「插得進去嗎」而不是「查不查得到資料行」——不然選一個函式就會把一段
跑不動的骨架整句蓋在使用者打的那一行上。

## 指令碼宣告的資料表

`#Loan` 與 `@rows` 的欄位中繼資料一列都查不到，但它們的宣告就在使用者眼前：

```sql
CREATE TABLE #Loan (Id INT IDENTITY(1,1) PRIMARY KEY, CopyNo NVARCHAR(20) NOT NULL);
DECLARE @rows TABLE (Id INT IDENTITY(1,1) PRIMARY KEY, CopyNo NVARCHAR(20) NOT NULL);
```

那份括號由 `Core/Parsing/SqlScriptTableCollector` 讀出來，接進
`SqlColumnSourceResolver`——於是**同一次修改讓四個位置一起活過來**：`SET |` 與
`WHERE |` 的欄位建議、`#Loan.` 與 `@rows.` 的欄位、`SELECT *` 按 Tab 的展開，
以及提交 `INSERT INTO`／`MERGE INTO` 之後的整句展開。各自接一條的話，
漏掉的那一條沒有徵兆，只是使用者在那個位置又得把每個欄位重打一遍。

只認**帶著資料行定義**的兩種寫法。`SELECT … INTO #Loan` 不在裡面：那裡沒有型別，
而少了型別的 `INSERT` 骨架會替使用者猜錯字面值——那張表的**名稱**仍然照列，
名稱與欄位是兩件事。`RETURNS @rows TABLE (…)` 則免費一起認得，
因為認的是「變數 `TABLE (`」這個形狀本身。

`CREATE TABLE` 這兩個字是必要條件而不是修飾：`INSERT INTO #Loan (CopyNo, ReaderId)`
的形狀與資料行清單一模一樣，少了前綴就會把使用者剛寫的 `INSERT` 讀成一份宣告，
而那份假宣告裡每個欄位都沒有型別，還會蓋掉真正的那一份。

一般資料表（`CREATE TABLE dbo.Loan (…)`）也不收：它在中繼資料裡，
而那一份回答的是「現在長什麼樣」，指令碼裡這一份回答的是「正要變成什麼樣」。

讀出來的資料行在 `Metadata/Model/SqlScriptTableDetail` 換成中繼資料層的欄位模型，
目的只有一個——**不要有第二份「哪些欄位插得進去」**。換過來之後，暫存資料表與
資料表變數走的就是資料庫物件那一份展開，[排除規則](completion-commit-expansion.md#哪些欄位插不進去)與
[值先填什麼](completion-commit-expansion.md#值先填什麼)一個字都不必重寫。

## 只有開啟查詢的括號才切開範圍

範圍以括號界定，但**不是每一個括號都算**。判斷的依據是左括號後面接什麼：

| 寫法 | 是不是新範圍 |
|---|---|
| `IN (SELECT … FROM Child c)` | 是，子查詢自己帶 FROM 子句 |
| `FROM (SELECT …) d` | 是 |
| `COUNT(a.…)`、`ISNULL(a.…, 0)` | 否，只是函式引數 |
| `WHERE (a.… = 1)` | 否，只是運算優先權 |
| `IN (1, 2, 3)` | 否，只是清單 |
| `INSERT INTO t (…)` | 否，只是資料行清單 |

一開始每個括號都當成子查詢，症狀是彙總函式裡完全沒有建議：

```text
SELECT COUNT(a.| FROM dbo.PUBLISHER a
```

範圍只剩括號裡那一段，看不到 `FROM dbo.PUBLISHER a`，別名 `a` 解析不出來就退回
「`a` 是結構描述」的解讀——而沒有任何物件屬於名為 `a` 的結構描述，
於是清單是**空的**。同一個原因也讓 `WHERE (a.`、`IN (a.`、`ISNULL(a.` 全都沒有欄位。

規則的兩半必須一起成立：分不出「開啟查詢的括號」與「運算式的括號」的話，
修好彙總函式就會弄壞子查詢，內層的別名會解析到外層的資料表去。

順帶的好處是 `INSERT INTO t (|)` 也列得出 `t` 的欄位——那個括號同樣不是子查詢，
而那正是使用者在那個位置要的東西。

## 詞元一結束就把清單重開

平台的規則是「沒有 session 就問建議來源要不要開，已經有 session 就只重新篩選」。
這對識別字是對的：多打一個字母只是把候選變少。但**結束詞元的字元**不是——
它會讓上下文整個換掉，而還開著的那份清單是照舊上下文組出來的：

```text
SELECT a       → 清單開著，裡面是關鍵字與資料庫物件
SELECT a.      → 平台拿 a. 去比對同一份清單，一個都比不中，清單默默關掉
SELECT a.N     → 這時才重新問來源，欄位清單終於出現
```

因此輸入這類字元時自己把 session 收掉再開一次。判斷放在 `SqlCompletionTriggers`，
只看文字不碰編輯器，可以完整單元測試：

- 前一個字元還能構成識別字（`SELECT CUST|`）→ 不重開，平台自己的篩選是對的。
- **小老鼠除外**：它構得成識別字（`@@ROW` 的詞元起點必須落在第一個小老鼠上），
  但打出來的那一刻目標會整個換掉。`INSERT INTO ` 開著的是資料表清單，
  `INSERT INTO @` 要的是使用者自己宣告的變數，兩份沒有一項重疊——症狀是
  「單打一個 `@` 什麼都沒有，`@S` 才有提示」。`@@` 同理，那又是另一份封閉清單。
  判斷與呼叫端共用 `SqlCompletionTriggers.MayChangeContext`：一邊放行、
  另一邊擋掉，等於沒改。
- 有限定字（`a.`、`dbo.`、`[dbo].`）→ 重開。
- 前方關鍵字已經指定了物件類別（`FROM `、`JOIN `、`EXEC `、`USE `、
  `ALTER PROCEDURE `）→ 重開。少了這一條，`SELECT * FROM |` 也要再多打一個字母
  才列得出資料表——與點號完全同一個病。
- 其餘（`SELECT `、`COUNT(`、`SELECT a.X, `）→ 不重開。

最後一條不是遺漏，是「輸入幾個字元之後才開始建議」那個設定在說話：前綴是空的，
而觸發字元數最少是 1，建議來源本來就不會參與。在這裡先擋掉只是省下一次白跑。

小數點也不算：`12.` 的點號前面是數字而不是識別字，分不出來的代價是每次輸入
小數點都彈出整個資料庫的物件清單。

## 重開清單的三個步驟

片段接續與分隔字元走的是同一段程式（`SqlCompletionReopen`），三個步驟一個都不能少：

```csharp
broker.GetSession(view)?.Dismiss();                  // 1
var session = broker.TriggerCompletion(view, trigger, caret, token);   // 2
session?.OpenOrUpdate(trigger, caret, token);        // 3
```

1. **先收掉舊的。** `TriggerCompletion` 一開頭就先問 `GetSession`，只要還有 session
   就原封不動把它交回來——不先收掉，整個呼叫沒有任何作用。`Dismiss` 是同步的，
   回傳之前就已經把自己從 broker 的紀錄裡拿掉。
2. **`TriggerCompletion` 只是建立 session**：問過各個來源要不要參與、算出適用範圍，
   然後就結束了。
3. **`OpenOrUpdate` 才會去要清單並把 UI 畫出來。** 少了這一行，前面每一步都算對了，
   畫面上仍然什麼都不會出現。平台自己的命令處理常式在同一個位置也是這樣接著寫的。

整段排在派送佇列的 Background 優先權上執行，不在原地直接呼叫：提交當下平台正要
把 session 收掉，而輸入字元當下那個字元還沒進緩衝區——在原地開出來的清單，
看到的是上一個狀態。

緊接在 `FROM`、`JOIN`、`EXEC` 之後的限定字一律當結構描述：
`FROM dbo.` 要列出 dbo 的物件，而 `FROM u.` 這種寫法並不存在。

沒有限定字的位置（`SELECT |`、`WHERE |`、`ON |`）也會列出敘述看得到的欄位，
而且排在資料庫物件之前——在這些位置要的幾乎都是欄位。這裡走的是同一個
解析器，所以子查詢、CTE、暫存資料表與資料表變數的欄位一樣列得出來；解析不出來的
那一個來源跳過，其他來源照列——與限定字的位置不同，這裡少列一個來源的欄位
不影響其他來源的正確性。

敘述裡有兩個以上**相異的限定字**時，插入的文字會自動補上別名，否則
`SELECT Name FROM A a JOIN B b` 會因為欄位名稱模稜兩可而執行失敗。
數的是相異的限定字而不是來源數量：`FROM (SELECT Id, * FROM T t) d` 攤平出兩個
來源，但它們都叫 `d`。

這條路徑只使用**已經在快取裡**的欄位，不會為了列清單去等一次查詢；沒命中就
這一輪不顯示，背景預先載入補上之後下一次按鍵就有了。指令碼裡讀得出來的欄位
不必等任何東西，一律當場就有。

## 一次詞法分析，兩個答案

「敘述看得到哪些欄位來源」與「限定字指向哪些欄位」由同一次分析算出來，
一起掛在上下文上。呼叫端各自再掃一次同一份文字的話，每按一鍵就要多剖析
整份指令碼一遍。

同一個理由，`InitializeCompletion` 與提交路徑改用只吃游標前文的多載：
前者要的是適用範圍與要不要參與，後者要的是限定字與 `ALTER` 的關鍵字起點，
沒有一項需要看游標後方。全文分析只留在真的需要解析別名的那一次。
