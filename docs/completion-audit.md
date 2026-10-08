# 召回稽核

本頁包含召回稽核的方法、語料、連線、執行、輸出與審查；守 `RecallCorpus.sql` 的單元測試見
[關鍵字](completion-keywords.md#召回稽核)。

## 方法

不靠人挑案例、不把 SSMS 當真值：拿真實完整的 SQL，在每個詞元起點截斷，走產品同一條路徑
（`SqlCompletionCandidates`）問「這裡列什麼」，作者寫的下一個詞
就是答案。判定在 `tools/SqlAssist.CompletionAudit`，單元測試共用。

- 判定逐批做；問清單時游標前是整段文字，與編輯器相同（前一批的 `CREATE TYPE` 看得到）。
- 空前綴就會自己開的位置（`SqlCompletionPolicy.IsClosed`）清單在那時建好、打字只篩選，所以拿空前綴的
  候選；其餘拿打了第一個字元時的候選。答案要在排名與上限之後**看得到**。
- 漏分三種：清單不開（Closed）、不在候選（Absent）、候選有但打字後看不到（Hidden）。
- 比對不分大小寫、去方括號；多字的建議項在第一個字列出就算，多段名稱比最後一段。
- 不該列的分類排除：常值（含緊貼數值的字：`20MB` 在編輯器裡是在寫數字；EXEC 引數與參數預設的 `true`）、新取的名稱（別名與標籤定義、CREATE 目標、宣告）、查不到存在的名稱、截斷的盲點、
  範例佔位符（`<login_name>`）起到那一句結束，`DATENAME(datepart`、名稱位置的保留字（`FROM Table`）只排除那個詞。新名字與
  引用範圍用 ScriptDom 另外認，不借產品的判斷：產品把該列的位置判成新名字，正是要抓的漏。
- ScriptDom 剖析不過的句子，從它停下的那個詞起到那一句結束不稽核：那裡不是 T-SQL（文件的語法
  片段、打錯的範例），產品判斷不了要什麼。只排除那一句、不排除整個批次：錯之前的每一格仍是合法語句的開頭，
  錯在最後一個詞或結尾算寫到一半；挖掉那一句再剖析，之後的句子照常稽核。整批丟掉會讓寫錯一句的指令碼
  整段消失、高估召回。句數記在總結的「剖析失敗」；ScriptDom 還不認得的新語法（`BACKUP MASTER KEY TO URL`）
  也在其中，所以召回語料必須剖析得過。錯之前不是保留字的字也不稽核：那一句不在語法樹上，
  `INSERT INTO t (Name` 的 `Name` 說不出是欄位還是字；寫到一半的那一句同理，只挖掉它，之前的句子才有語法樹。
- 名稱要知道存在才稽核：沒有連線時只稽核指令碼自己取的（別名、CTE、變數、暫存資料表）。召回語料守
  「字大寫、名稱大小寫混合」，看寫法：大小寫混合的一律是名稱（片語收了 `COPY`，`Copy` 仍是資料表），全小寫的照字的名單；其餘語料看語法樹上的角色，欄位 `name`、`type` 不當成字，型別名稱只有內建型別是字。
  點號之後還要限定字、或別名指的資料表認得出來：名稱索引只比名字，別的資料庫的表碰巧有同名欄位不算漏；
  別名的來源寫了資料庫時，那個資料庫也要在連線的伺服器上；暫存資料表是 `SELECT * INTO` 一張表時看那張表。
  沒寫限定字的欄位同理，它可能屬於的表至少要認得一張：資料行清單（INSERT／MERGE 的資料行、SET 的左邊、索引、
  條件約束與統計資料的欄位）只屬於指定的那張表；查詢裡的屬於這一層與外層的來源，衍生資料表看不到它那一層（APPLY 右邊
  除外）；T-SQL 由內往外找，一層裡有寫在看不到的資料庫的表時，那張表一定存在、欄位可能是它的，外層的不算
  （名稱索引沒有的表不存在，不擋）；來源有衍生資料表、函式或資料表變數時說不出來，照常稽核。DML 目標寫成 FROM 的別名時就是那張表。`CREATE TABLE` 自己的條件約束與索引清單
  引用的是同一份定義的資料行，算指令碼的名稱。CTE 與衍生資料表的資料行清單是那張表的欄位，照同一條規則：
  沒寫限定字時那張表要是這個欄位可能屬於的表之一，點號之後限定字要指它；CTE 自己的查詢裡同名的詞是別張表的欄位。
- 選取清單的別名只在查詢自己的 ORDER BY 引用得到，視窗與 `WITHIN GROUP` 的 ORDER BY 裡同名的詞是來源的欄位；
  具名視窗在它那個查詢裡引用得到，寫在 WINDOW 子句之前的 `OVER w` 歸截斷的盲點。
- 資料來源的一段名稱只指 CTE 與暫存資料表，不指外層別名（DML 的目標除外）。`::` 之後的成員（型別的靜態成員、安全性實體）不是指令碼取的。
- `inserted`／`deleted` 在 DML 觸發程序那一句（指父資料表）與 OUTPUT 子句（指 DML 的目標）裡算指令碼的名稱，
  形狀寫字面大寫、不遮，自成一群；範圍外的同名詞照一般名稱判斷。
- 只在漏的位置問 SSMS 的 `Resolver.FindCompletions` 當第二意見：沒有繫結時一項都不回，繫結後只列名稱、
  不列關鍵字，所以只問名稱。「SSMS 有、我們沒有」是強訊號，「兩邊都沒有」待判。

截斷的盲點由判定排除：截斷處還沒有資料來源，兩邊都列不出來。選取清單寫在 FROM 之前、限定字是之後才取的別名；
限定字是 CTE 名稱，遞迴 CTE 第二段的選取清單寫在 FROM 之前；外層取過同名別名，子查詢之後才取的遮住外層；
DML 的目標是 FROM 才取的別名（`UPDATE l SET … FROM Loan l`），SET 與 OUTPUT 的 `inserted.`／`deleted.` 不知道是哪張表。
不逐群標 `ignore`，因為同一個簽章裡還混著真的漏。

## 語料

| `-Sources` | 內容 |
|---|---|
| `recall` | `RecallCorpus.sql`；必須零漏，單元測試也守 |
| `memory` | SQL Memory 的執行歷史，經擴充同一份路徑與讀取 API 唯讀讀取 |
| `modules` | 連線資料庫的 `sys.sql_modules`（程序、檢視、函式、觸發程序） |
| `microsoft` | SqlScriptDOM 的剖析器測試腳本（MIT）、sql-docs 的 T-SQL 範例（CC-BY-4.0，程式碼 MIT） |

微軟語料釘在 `tools/SqlAssist.CompletionAudit.Runner/corpora.json` 的 commit，第一次用到才下載到
`artifacts/completion-audit/corpora/`，旁邊的 `manifest.json` 記來源與授權；不進版控，換語料就改 commit。
新增來源實作 `IAuditCorpusSource`。

## 連線與執行

```powershell
.\tools\Set-CompletionAuditConnection.ps1                   # 互動輸入；重跑就是修改，-Show 只印非機密欄位
.\tools\Audit-Completions.ps1 -Sources recall -MaxMinutes 5 # 先確認工具正常
.\tools\Audit-Completions.ps1 -MaxMinutes 60                # 全部語料
```

連線設定與名稱代號金鑰在 `%LOCALAPPDATA%\SqlAssist.Ssms22\CompletionAudit\`。密碼以 DPAPI（目前使用者）
加密，只交給 `SqlCredential`；登入名稱輸入 `-` 是 Windows 驗證，建議用只有 CONNECT 與 VIEW DEFINITION
的唯讀登入。沒設定時 `modules` 略過。腳本先建置執行器；有漏不算失敗，只有
工具錯誤才非零結束碼。

| `artifacts/completion-audit/` | 內容 |
|---|---|
| `<yyyyMMdd-HHmm>/clusters.md` | 第一行總結；只列新群與退化，最多 30 群：識別、次數、SSMS 列出比例、最短範例（≤3 行，游標 `⎵`） |
| `<yyyyMMdd-HHmm>/raw.jsonl` | 完整紀錄與名稱對照表；**AI 不讀** |
| `state.json` | 每群的分類：`gap`、`ignore`、`fixed` 加理由 |
| `cache/results.jsonl` | 每段的結果；語料、判定組件與目錄指紋不變就不重跑 |

群的識別是位置簽章（前兩個詞元的形狀、答案、漏的樣子）的雜湊，只看文字，產品改版不換號。名稱預設換成
本機金鑰算的穩定代號（`T_` 物件、`C_` 欄位、`A_` 指令碼取的名稱、`N_` 不明），註解與常值遮掉。

## 審查

使用者說「審查最新一次召回稽核」時照做：

1. 只讀最新一份 `clusters.md` 與 `state.json`，不讀 `raw.jsonl`。
2. 每一群決定：實作、忽略或需使用者決策；前兩者寫進 `state.json`，第三種列出來問。

   ```json
   { "clusters": { "1c63f62a": { "status": "gap", "reason": "SET 之後的 @ 沒有列變數" } } }
   ```

   直接標 `ignore`、理由寫明哪一種：Azure Synapse／PDW 才有的語法、已淘汰的功能（Stretch、HTTP／SOAP 端點）、
   sql-docs 沒有記載而只在 ScriptDom 測試腳本出現的語法。

3. 稽核自己的誤判改 `tools/SqlAssist.CompletionAudit` 的判定，不標 `ignore`。判定新認得的名稱會讓召回語料
   轉紅時，與補上它的產品修正放同一批（認得 `inserted`／`deleted` 時，產品同一個 commit 列出它們）。
4. 修好之後 `-Cluster <識別>` 重驗，仍漏 0 才改成 `fixed`。在 worktree 裡修時先把
   主工作區的 `artifacts\completion-audit` 以 junction 連過去，`-Cluster` 與 `state.json` 才是同一份。
5. 標 `fixed` 的群再出現就是退化，排在報告最前面。
