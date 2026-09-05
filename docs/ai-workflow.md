# AI 開發：先看這頁

**直接開 Claude Code 或 Codex 就能工作，不必先安裝 RTK，也沒有專案初始化腳本。**
Claude 讀 [CLAUDE.md](../CLAUDE.md)；Codex 由 [AGENTS.md](../AGENTS.md) 讀同一份規則。
兩者都先查 [文件索引](index.md)，再讀本次修改需要的章節，不先讀全庫。

## 腳本用途

AI 相關只保留三個 PowerShell 腳本，需 **PowerShell 7**，不依賴 RTK 或模型 API。

| 腳本 | 白話用途 | 什麼時候需要 |
|---|---|---|
| [Read-Context.ps1](../tools/Read-Context.ps1) | 長檔分段讀，避免一次塞給 AI | AI 要讀長文件時 |
| [Invoke-QuietCommand.ps1](../tools/Invoke-QuietCommand.ps1) | 完整命令紀錄留在磁碟，只給 AI 看短摘要 | AI 跑建置、測試等大量輸出時 |
| [Test-AgentWorkflow.ps1](../tools/Test-AgentWorkflow.ps1) | 檢查前兩個助手及文件檢查器沒有壞掉 | 維護工具、或啟用 pre-push 檢查時 |

**你不用每天手動跑這三個。** 原本的 `Run-CoreTests.ps1` 才是產品單元測試，
`Build-Extension.ps1` 才是建置 VSIX；用法仍在 [開發文件](development.md#建置與測試)。

## 輸出分工

執行前先決定這一輪需要「概覽、定位、原文或驗證」，再選命令；不把所有命令機械地
包成 `rtk proxy`。以下範例假設 RTK 已可用；未安裝時直接使用原生命令及既有助手。

| 目的 | 預設做法 | 升級為原文的時機 |
|---|---|---|
| 工作區／變更概覽 | `rtk git status`、`rtk git diff --stat`、`rtk git log -5` | 先分清已暫存與未暫存變更，不能把統計當成審查 |
| 找檔案或符號 | 先限制目錄、檔名或 `rg -l`；探索可用 `rtk rg` | 要確認全部引用／宣稱不存在時，用限定範圍的無損搜尋 |
| 理解實作／護欄 | 定位行號後用 `Read-Context.ps1` 分段 | 必讀護欄與相關實作要讀完；短檔才一次讀取 |
| 建置／測試 | `Invoke-QuietCommand.ps1` 執行完整流程 | 失敗後依紀錄路徑讀錯誤附近，不重貼整份紀錄 |
| 最終審查 | 依變更檔案分批讀無損 diff | 檢查本輪全部變更；不得以壓縮 diff 代替 |

```powershell
# 概覽先看範圍，不先印出所有差異。
rtk git diff --stat
# 長 diff 只需要內容概覽時，才使用 RTK 的有損壓縮。
rtk git diff -- CLAUDE.md
# 只回傳候選檔名；proxy 保留原文，省輸出的是查詢範圍。
rtk proxy rg --files src -g '*Theme*.cs'
# 確定要審查這個檔案後才讀完整差異。
rtk proxy git diff --no-ext-diff -- CLAUDE.md
```

- 需要已暫存差異時加 `--cached`；不要反覆讀取與本輪無關的既有變更。
- `proxy` 只用於需要無損輸出、RTK 不支援的命令或既有節流助手；不是壓縮模式。
- 輸出太大就縮小檔案／行號範圍或保存完整紀錄後分段讀，不提高 token 上限硬塞，
  也不把多個長檔併成一次輸出。已讀且未變的區段不重讀。
- 不以有損摘要的空結果證明「不存在」。工具回報截斷時必須補讀缺漏範圍。
- RTK 已確認可用就不反覆執行版本／統計查詢；不要為追求節省率另跑一次相同命令。

## 文件按需讀取

先查標題／檔名，再讀命中區段。跨功能修改仍須補齊相關護欄，不能因節流而漏讀。

在專案根目錄的 PowerShell 7 執行：

```powershell
# 先定位章節，避免為了找一段規則而讀整份文件。
rtk proxy rg -n 'MEF' docs/rules-platform.md
rtk proxy pwsh -NoProfile -File tools/Read-Context.ps1 -Path docs/rules-platform.md -StartLine 1 -LineCount 40
```

預設最多 80 行、約 4500 字元，會附行號及續讀位置；Markdown 單檔另由檢查器限制在
4000 字元。超長單行不硬切；需要時按資料格式
抽取欄位。搜尋片段不是完整證據，空結果也不代表沒有其他引用。

## 工具輸出節流

```powershell
# 執行的仍是原本流程，不為省 token 另做一套不完整的測試。
rtk proxy pwsh -NoProfile -File tools/Invoke-QuietCommand.ps1 -ScriptPath tools/Run-CoreTests.ps1
rtk proxy pwsh -NoProfile -File tools/Invoke-QuietCommand.ps1 -ScriptPath tools/Build-Extension.ps1
```

- `-ScriptPath` 用於 PS1；原生執行檔用 `-Command`，額外引數用 `-Arguments @(...)`。
- 預設顯示最後 20 行、整份預覽約 6000 字元；完整 stdout／stderr 與結果存在
  `artifacts/ai-logs/`。**artifacts 是紀錄與暫存，不是設定，不進 Git。**
- 失敗先按輸出的紀錄路徑查原因，不重新把完整紀錄貼給 AI。原始紀錄保留原編碼；
  亂碼時可指定 `-PreviewEncoding`，不能假設所有紀錄都是 UTF-8。
- 保留原命令結束碼；預設 900 秒逾時回傳 124，包裝器本身失敗回傳 125。
  外層腳本也須保留 `$LASTEXITCODE`，不能只比對「成功」字樣。
- **不包互動命令、安裝精靈或 MCP 常駐伺服器**；不改測試範圍、不吞失敗。
- 這裡的短輸出由助手產生，不是 `rtk proxy` 壓縮；何時保留原文見上方輸出分工。

## 換設備：哪些會跟著 Git

| 項目 | 處理方式 |
|---|---|
| 文件、上述三個腳本 | 隨 Git 共用，clone 後可直接沿用 |
| [.claude/settings.json](../.claude/settings.json) | 共用 RTK Hook；有 RTK 才執行，沒裝就略過 |
| `.codex/config.toml`、`.mcp.json` | 本機 MCP 登記，可能含機器路徑，不進 Git |
| `.claude/settings.local.json` | 個人覆寫，不進 Git |
| `artifacts/` | 紀錄及暫存，不進 Git |

**換設備只要 clone、登入客戶端；需要加速工具時才照下面安裝。**
不複製別台電腦的登入資料、全域設定或絕對路徑，不需要同步暫存紀錄。

## 選用工具

- [RTK 短教學](ai-rtk.md)：壓縮命令輸出；Claude 用共用 Hook，Codex 依共用規則使用。
- 沒安裝 RTK 時回退原生命令，不自動安裝。

## 維護時驗證

```powershell
# 這些檢查不呼叫模型，用來守住工具行為、文件連結與文字格式。
pwsh -NoProfile -File tools/Test-AgentWorkflow.ps1
pwsh -NoProfile -File tools/Check-Docs.ps1
pwsh -NoProfile -File tools/Check-TextFiles.ps1
```

`Test-AgentWorkflow.ps1` 也會在 RTK 可用時實測壓縮與無損路徑，並保留前後輸出；
細節見 [RTK 驗證](ai-rtk.md#測試與排錯)。新任務仍須檢查實際命令是否遵循上方分工。
字元縮減與 RTK 統計都不是整個 task 的精確 token／帳單；比較時也要看是否漏改或重試。
