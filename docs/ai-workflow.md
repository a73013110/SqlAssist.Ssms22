# AI 共用工作流程

範圍：Claude Code 與 Codex 的文件讀取、非互動命令輸出與回歸驗證。
返回 [索引](index.md)。這套基礎流程不需要 RTK、Serena、MCP、API 金鑰或背景服務。

## 兩個客戶端只維護一套規則

- Claude Code 使用根目錄 [CLAUDE.md](../CLAUDE.md)。
- Codex 先讀 [AGENTS.md](../AGENTS.md)，再依其要求讀同一份 CLAUDE.md。
- 不另外複製一份完整規則到全域設定、Skill 或 MCP 提示；不讓兩個入口互相全文匯入。
- 修改入口後開新 task／重新啟動客戶端驗證，不假設進行中的 task 已替換舊上下文。

這符合 [Codex 的 AGENTS.md 發現機制](https://learn.chatgpt.com/docs/agent-configuration/agents-md)
與 [Claude 的按需上下文建議](https://code.claude.com/docs/en/costs)。

## 文件按需讀取

1. 讀短索引，按**實際變更範圍**選擇必讀護欄及功能文件。測試依被測功能比照。
2. 先查標題、符號或檔名，再讀區段。跨領域時補讀相關規則，不因節流漏讀護欄。
3. 不知道精確路徑才查詳細程式碼地圖；不要每次開場讀所有資料夾表與共用表。
4. 根據續讀提示補足證據；片段、搜尋前幾筆與符號索引都不是「沒有其他引用」的證明。

```powershell
Set-Location 'D:\GitProject\SqlAssist.Ssms22'

# 先看標題，避免為了找一個章節就讀完整份文件。
rg -n '^## ' docs/change-rules.md
$section = Select-String -LiteralPath 'docs/change-rules.md' -Pattern '^## 平台接線$'
.\tools\Read-Context.ps1 -Path 'docs/change-rules.md' -StartLine $section.LineNumber -LineCount 60

# 先列命中的檔案，再針對必要檔案查行號與內容。
rg -l -F 'SqlTrivia.Skip' src/SqlAssist.Core tests/SqlAssist.Core.Tests
rg -n -F 'SqlTrivia.Skip' src/SqlAssist.Core/Parsing
```

`Read-Context.ps1` 的預設值為 80 行／6000 字元，上限為 8000 字元，會附行號及下一段
起點。不截斷半行：若單行超過預算，回傳 2，應先用 JSON／XML 等本機工具抽取所需欄位。
找不到檔案、無效 UTF-8 或起點超出檔尾回傳 1；空檔是正常結果。
字元是可驗證的輸出預算，不是精確 token 計數。

## 工具輸出節流

既有建置、測試腳本與驗證範圍不變。只在它們外面加共用輸出包裝器：

```powershell
# 保留原流程，避免為 AI 另維護一套測試清單。
pwsh -NoProfile -File tools/Invoke-QuietCommand.ps1 -ScriptPath tools/Run-CoreTests.ps1
pwsh -NoProfile -File tools/Invoke-QuietCommand.ps1 -ScriptPath tools/Build-Extension.ps1
pwsh -NoProfile -File tools/Invoke-QuietCommand.ps1 -ScriptPath tools/Check-Docs.ps1
```

需要傳引數時，在 PowerShell 中用陣列，不把命令拼成一個字串：

```powershell
# 分開傳遞引數，含空白的路徑不會被重新切開。
.\tools\Invoke-QuietCommand.ps1 -ScriptPath 'tools/Run-CoreTests.ps1' `
    -Arguments @('-Configuration', 'Debug')

# 也能包原生執行檔；互動命令、MCP 常駐程序與安裝精靈不適用。
.\tools\Invoke-QuietCommand.ps1 -Command git -Arguments @('diff', '--check')
$code = $LASTEXITCODE
"原命令結束碼：$code"
```

- 每次在 `artifacts/ai-logs/<時間與唯一識別碼>/` 建立 `stdout.log`、`stderr.log` 與 `result.json`。
- 兩條串流同時排空到磁碟，保留完整原始位元組；預覽移除 ANSI 控制碼，不改寫原始紀錄。
- 預設各取最後 20 行，**整份預覽**最多約 6000 字元，stderr 優先；`-TailLines 0` 只看狀態。
- 預設最多執行 900 秒，可用 `-TimeoutSeconds` 調整。逾時終止子程序樹，明確回傳 124；
  包裝器本身無法啟動或寫檔回傳 125。其他情況原樣回傳子程序結束碼，不從輸出文字猜測。
- 組合多個命令的 CI／外層腳本要保存 `$LASTEXITCODE`，最後 `exit $code`；不要讓後續成功
  的 `Write-Output` 或檢查命令掩蓋失敗。互動終端機則不必 `exit`。
- 不傳 `.cmd`／`.bat` 讓包裝器隱式啟動另一個 shell；PS1 使用 `-ScriptPath`。
- 不會自動刪除紀錄。原始內容可能含路徑、連線或敏感資料，分享前先審閱；不得提交。

失敗時先根據結果路徑定位，而不是把完整紀錄重新貼給 AI，或為取回紀錄重跑昂貴命令：

```powershell
# 將路徑換成剛才印出的完整紀錄檔，先找重要行，再用 Read-Context 讀相鄰區段。
$log = 'D:\GitProject\SqlAssist.Ssms22\artifacts\ai-logs\本次目錄\stdout.log'
Select-String -LiteralPath $log -Pattern 'error|failed|exception|錯誤|失敗' |
    Select-Object -First 20 LineNumber, Line
```

原始紀錄可能使用原命令的編碼，並非保證 UTF-8；預覽在最多 128 KiB 的尾窗內逐行嘗試
嚴格 UTF-8，失敗後回退系統 OEM，並標示採用的編碼，避免 Windows 建置混合串流亂碼。
超出尾窗的殘缺長行會明確標示省略。可用 `-PreviewEncoding utf8`／`oem` 或實際編碼名稱覆寫。
未知、UTF-16 或同一行內混合編碼仍須按原工具規格讀取，不能把猜測當成保證。
只有 `result.json` 與專案文字來源保證 UTF-8（無 BOM）／LF。
這層不過濾 Microsoft.Testing.Platform 的測試結果、不改執行器，也不取代測試本身。

## 驗證與量測

```powershell
# 先驗證工具的失敗語意，再跑既有專案檢查。
pwsh -NoProfile -File tools/Test-AgentWorkflow.ps1
pwsh -NoProfile -File tools/Check-Docs.ps1
pwsh -NoProfile -File tools/Check-TextFiles.ps1
pwsh -NoProfile -File tools/Invoke-QuietCommand.ps1 -ScriptPath tools/Run-CoreTests.ps1
```

工具測試不需下載測試框架或呼叫模型：驗證大批 stdout／stderr、原始紀錄、Unicode、
空白與引號引數、非零結束碼、逾時、空輸出、讀取預算、壞連結與入口預算。
它的字元縮減示例只代表合成命令輸出，不宣稱整個 task 的 token 節省率。

Claude Code 與 Codex 各開一個新 task，要求「依共用規則定位 SqlTrivia 的文件與程式位置，
再透過節流包裝器執行文件檢查；不要讀全庫，也不要改檔」。檢查實際工具紀錄，而不是只聽
模型宣稱有遵守。再用相同模型、同一 commit 與相同任務，比較總用量及是否漏改、重試。
先只改一個因素；不要把帳號整段時間的額度下降當成某個 task 的精確用量。

## 選用工具

- [RTK](ai-rtk.md)：壓縮常見命令輸出；先採共用規則引導，Claude Hook 是額外選項。
- [Serena](ai-serena.md)：C# 符號檢索；先採唯讀 MCP，原生編輯與驗證仍保留。
- 不同時讓兩個工具改寫同一條命令。Microsoft.Testing.Platform 與建置仍走本文件的包裝器。
- 完整 diff、需要列出所有命中的搜尋、必要護欄及原始錯誤，不使用有損摘要代替。
  若已啟用 RTK Hook，完整原生命令需以 `rtk proxy` 繞過自動過濾。
