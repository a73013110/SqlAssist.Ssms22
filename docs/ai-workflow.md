# AI 開發：先看這頁

**直接開 Claude Code 或 Codex 就能工作，不必先安裝 RTK／Serena，也沒有專案初始化腳本。**
Claude 讀 [CLAUDE.md](../CLAUDE.md)；Codex 由 [AGENTS.md](../AGENTS.md) 讀同一份規則。
兩者都先查 [文件索引](index.md)，再讀本次修改需要的章節，不先讀全庫。

## 腳本用途

AI 相關只保留三個 PowerShell 腳本，需 **PowerShell 7**，不依賴 RTK、Serena 或模型 API。

| 腳本 | 白話用途 | 什麼時候需要 |
|---|---|---|
| [Read-Context.ps1](../tools/Read-Context.ps1) | 長檔分段讀，避免一次塞給 AI | AI 要讀長文件時 |
| [Invoke-QuietCommand.ps1](../tools/Invoke-QuietCommand.ps1) | 完整命令紀錄留在磁碟，只給 AI 看短摘要 | AI 跑建置、測試等大量輸出時 |
| [Test-AgentWorkflow.ps1](../tools/Test-AgentWorkflow.ps1) | 檢查前兩個助手及文件檢查器沒有壞掉 | 維護工具、或啟用 pre-push 檢查時 |

**你不用每天手動跑這三個。** 原本的 `Run-CoreTests.ps1` 才是產品單元測試，
`Build-Extension.ps1` 才是建置 VSIX；用法仍在 [開發文件](development.md#建置與測試)。

## 文件按需讀取

先查標題／檔名，再讀命中區段。跨功能修改仍須補齊相關護欄，不能因節流而漏讀。

在專案根目錄的 PowerShell 7 執行：

```powershell
# 先定位章節，避免為了找一段規則而讀整份文件。
Select-String -Path docs/change-rules.md -Pattern '^## '
./tools/Read-Context.ps1 -Path docs/change-rules.md -StartLine 1 -LineCount 40
```

預設最多 80 行、約 6000 字元，會附行號及續讀位置。超長單行不硬切；需要時按資料格式
抽取欄位。搜尋片段不是完整證據，空結果也不代表沒有其他引用。

## 工具輸出節流

```powershell
# 執行的仍是原本流程，不為省 token 另做一套不完整的測試。
pwsh -NoProfile -File tools/Invoke-QuietCommand.ps1 -ScriptPath tools/Run-CoreTests.ps1
pwsh -NoProfile -File tools/Invoke-QuietCommand.ps1 -ScriptPath tools/Build-Extension.ps1
```

- `-ScriptPath` 用於 PS1；原生執行檔用 `-Command`，額外引數用 `-Arguments @(...)`。
- 預設顯示最後 20 行、整份預覽約 6000 字元；完整 stdout／stderr 與結果存在
  `artifacts/ai-logs/`。**artifacts 是紀錄與暫存，不是設定，不進 Git。**
- 失敗先按輸出的紀錄路徑查原因，不重新把完整紀錄貼給 AI。原始紀錄保留原編碼；
  亂碼時可指定 `-PreviewEncoding`，不能假設所有紀錄都是 UTF-8。
- 保留原命令結束碼；預設 900 秒逾時回傳 124，包裝器本身失敗回傳 125。
  外層腳本也須保留 `$LASTEXITCODE`，不能只比對「成功」字樣。
- **不包互動命令、安裝精靈或 MCP 常駐伺服器**；不改測試範圍、不吞失敗。
- 完整 diff／完整搜尋用原生命令；若 RTK Hook 已啟用，以 `rtk proxy` 繞過過濾。

## 換設備：哪些會跟著 Git

| 項目 | 處理方式 |
|---|---|
| 文件、上述三個腳本 | 隨 Git 共用，clone 後可直接沿用 |
| [.claude/settings.json](../.claude/settings.json) | 共用 RTK Hook；有 RTK 才執行，沒裝就略過 |
| [.serena/project.yml](../.serena/project.yml) | 共用 C#、唯讀及按需查詢規則；本身不會啟動 MCP |
| `.codex/config.toml`、`.mcp.json` | 本機 MCP 登記，可能含機器路徑，不進 Git |
| `.claude/settings.local.json`、`.serena/project.local.yml` | 個人覆寫，不進 Git |
| `.serena/` 其他內容、`artifacts/` | 快取、記憶、紀錄及暫存，不進 Git |

**換設備只要 clone、登入客戶端；需要加速工具時才照下面安裝。**
不複製別台電腦的登入資料、全域設定或絕對路徑，不需要同步暫存紀錄。

## 選用工具

- [RTK 短教學](ai-rtk.md)：壓縮命令輸出；Claude 用共用 Hook，Codex 依共用規則使用。
- [Serena 短教學](ai-serena.md)：查 C# 符號；**兩端共用本機 CLI，各自一份專案 MCP 設定**。
- 沒安裝、沒連線或查不到時，回退原生讀檔／搜尋，不自動安裝或重建全庫。

## 維護時驗證

```powershell
# 這些檢查不呼叫模型，用來守住工具行為、文件連結與文字格式。
pwsh -NoProfile -File tools/Test-AgentWorkflow.ps1
pwsh -NoProfile -File tools/Check-Docs.ps1
pwsh -NoProfile -File tools/Check-TextFiles.ps1
```

再各開一個 Claude／Codex task，檢查實際是否分段讀檔、使用短輸出，而非只聽模型宣稱。
字元縮減與 RTK 統計都不是整個 task 的精確 token／帳單；比較時也要看是否漏改或重試。
