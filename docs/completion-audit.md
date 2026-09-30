# 召回稽核

本頁包含召回稽核的方法、語料、連線、執行、輸出與審查；守 `RecallCorpus.sql` 的單元測試見
[關鍵字](completion-keywords.md#召回稽核)。

## 方法

不靠人挑案例，也不把 SSMS 當真值：拿真實完整的 SQL，在每個詞元起點截斷，走產品同一條路徑
（`SqlCompletionCandidates`，資料庫由 `ISqlCompletionMetadata` 注入）問「這裡列什麼」，作者寫的下一個詞
就是答案。判定在 `tools/SqlAssist.CompletionAudit`，單元測試與稽核工具共用。

- 空前綴就會自己開的位置（`SqlCompletionPolicy.IsClosed`）清單在那時建好、打字只篩選，所以拿空前綴的
  候選；其餘拿打了第一個字元時的候選（全域變數是 `@@`）。答案要在排名與上限之後**看得到**。
- 漏分三種：清單不開（Closed）、不在候選（Absent）、候選有但打字後看不到（Hidden）。
- 比對不分大小寫、去方括號；多字的建議項在第一個字列出就算，多段名稱比最後一段。
- 不該列的分類排除：常值、新取的名稱（別名定義、CREATE 目標、宣告）、查不到存在的名稱。新名字與
  引用範圍用 ScriptDom 另外認，不借產品的判斷——產品把該列的位置判成新名字，正是要抓的漏。
- 名稱要知道存在才稽核：沒有連線時只稽核指令碼自己取的（別名、CTE、變數、暫存資料表）。召回語料守
  「字大寫、名稱大小寫混合」，看寫法；其餘語料看語法樹上的角色，欄位 `name`、`type` 不當成字。
- 只在漏的位置問 SSMS 的 `Resolver.FindCompletions` 當第二意見：沒有繫結時一項都不回，繫結後只列名稱、
  不列關鍵字，所以只問名稱。「SSMS 有、我們沒有」是強訊號，「兩邊都沒有」待判。

截斷的盲點：選取清單寫在 FROM 之前時，別名與欄位兩邊都列不出來；這一類標 `ignore`。

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
的唯讀登入。沒設定時 `modules` 略過。執行前先建置執行器（稽核的就是目前這份程式）；有漏不算失敗，只有
工具錯誤才非零結束碼。

| `artifacts/completion-audit/` | 內容 |
|---|---|
| `<yyyyMMdd-HHmm>/clusters.md` | 第一行總結；只列新群與退化，最多 30 群：識別、次數、SSMS 列出比例、最短範例（≤3 行，游標 `⎵`） |
| `<yyyyMMdd-HHmm>/raw.jsonl` | 完整紀錄與名稱對照表；**AI 不讀** |
| `state.json` | 每群的分類：`gap`、`ignore`、`fixed` 加理由 |
| `cache/results.jsonl` | 每段的結果；語料、判定組件與目錄指紋不變就不重跑 |

群的識別是位置簽章（前兩個詞元的形狀、答案、漏的樣子）的雜湊，只看文字，產品改版不換號。名稱預設換成
本機金鑰算的穩定代號（`T_` 物件、`C_` 欄位、`A_` 指令碼取的名稱、`N_` 不明），註解與常值遮掉。
做完的段隨時進快取：時間到或 Ctrl+C 之後，下一次接著跑。

## 審查

使用者說「審查最新一次召回稽核」時照做：

1. 只讀最新一份 `clusters.md` 與 `state.json`，不讀 `raw.jsonl`。
2. 每一群決定：實作、忽略或需使用者決策；前兩者寫進 `state.json`，第三種列出來問。

   ```json
   { "clusters": { "1c63f62a": { "status": "gap", "reason": "SET 之後的 @ 沒有列變數" } } }
   ```

3. 修正一批一條分支；修好之後 `-Cluster <識別>` 重驗，仍漏 0 才改成 `fixed`。
4. 標 `fixed` 的群再出現就是退化，排在報告最前面。
