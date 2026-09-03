# Serena：C#、Claude Code 與 Codex

範圍：Serena 的安裝、唯讀設定、MCP 接線、符號使用與驗證。返回 [索引](index.md)；
一般讀取與建置仍走 [共用工作流程](ai-workflow.md)。
核對日期：2026-09-03；固定 [serena-agent 1.7.0](https://github.com/oraios/serena/releases/tag/v1.7.0)。
本教學不要求 API 金鑰，不自動產生全庫摘要，也不取代根目錄規範。

## 安裝一次

需要 PowerShell 7、uv、.NET 10+。C# 的 Roslyn 語言服務可能在首次使用時下載；
這是**工具的執行需求**，不需要把本專案的 netstandard2.0／net48 改成 .NET 10。
[C# 需求](https://github.com/oraios/serena/blob/v1.7.0/docs/01-about/020_programming-languages.md)

```powershell
# 已有對應工具就略過安裝；不要因終端機 PATH 尚未更新而重複安裝。
winget install --exact --id Microsoft.PowerShell
winget install --exact --id astral-sh.uv
```

重開 **PowerShell 7** 後：

```powershell
pwsh --version
uv --version
dotnet --list-sdks
# 隔離工具與專案相依；Python 由 uv 管理，不塞進 .NET 專案。
uv tool install --python 3.13 'serena-agent==1.7.0'
uv tool update-shell
# Windows 繁體中文環境預設為 CP950；若未啟用 UTF-8 模式，health-check 輸出 Emoji 會觸發編碼例外。
[Environment]::SetEnvironmentVariable("PYTHONUTF8", "1", "User")
```

再重開終端機並確認：

```powershell
Get-Command serena
serena --version
serena init
```

`serena init` 初始化使用者層級的 Serena 設定，可能提示信任或依賴下載；先審閱提示，
不要為了方便對所有專案開啟全域信任或自動批准。
安裝程序來源：[Serena 安裝](https://oraios.github.io/serena/02-usage/010_installation.html)、
[uv 的 WinGet 安裝](https://docs.astral.sh/uv/getting-started/installation/#winget)。

## 專案設定：兩個客戶端共用唯讀索引

```powershell
Set-Location 'D:\GitProject\SqlAssist.Ssms22'
# 已存在 .serena/project.yml 時不要重新生成或覆蓋，直接合併必要欄位。
serena project create . --language csharp --name SqlAssist.Ssms22
```

編輯 `.serena/project.yml`，保留生成器的其他欄位，合併以下設定。**1.7.0 的欄位是
`language_servers`，不要照舊文章改成 `languages`。**

```yaml
project_name: "SqlAssist.Ssms22"
language_servers: ["csharp"]
encoding: "utf-8"
line_ending: "lf"
ignore_all_files_in_gitignore: true
# 先驗證檢索品質；程式修改仍交給 Claude／Codex 原生編輯與既有測試。
read_only: true
excluded_tools:
  - execute_shell_command
initial_prompt: >-
  先遵守專案 CLAUDE.md 與 docs/index.md 的按需路由。
  先查符號概覽，再讀必要符號本體；不要全庫 onboarding、重建完整架構摘要，
  或把記憶檔當成比原始碼與既有文件更高的規範。
```

`read_only` 只限制 **Serena 的編輯工具**，不禁止主代理正常修改程式。
`.serena/` 已被本專案忽略；快取、記憶、下載紀錄與個人設定不提交。
保留測試與生成的關鍵字來源在分析範圍，避免因為只索引正式程式就漏掉使用者。
[1.7.0 設定樣板](https://github.com/oraios/serena/blob/v1.7.0/src/serena/resources/project.template.yml)

先在本機建立索引，避免首次 MCP 連線同時負擔依賴下載：

```powershell
# 這是本機語言服務索引，不是請 AI 讀完整個 repo。
.\tools\Invoke-QuietCommand.ps1 -Command serena -Arguments @('project', 'index', '.') `
    -TimeoutSeconds 1800
.\tools\Invoke-QuietCommand.ps1 -Command serena -Arguments @('project', 'health-check', '.') `
    -TimeoutSeconds 1800
```

先確認索引命令的 `$LASTEXITCODE` 為 0 才執行 health-check；失敗就依輸出的完整紀錄排查。
health-check 驗證符號概覽、定位與引用，不是完整專案正確性的證明。
若索引／檢查失敗，不能把「MCP 已連線」當成 C# 可用。
[CLI 實作與旗標](https://github.com/oraios/serena/blob/v1.7.0/src/serena/cli.py)

## Claude Code 設定

在專案根目錄執行，採用 **local scope**，只作用於這個專案、不共用到其他 repo：

```powershell
$project = (Get-Location).Path
$serena = (Get-Command serena -CommandType Application).Source
# 固定專案與實際執行檔，避免 App 的工作目錄或 PATH 不同而載入錯的程式庫。
claude mcp add --scope local --transport stdio serena -- $serena start-mcp-server `
    --context claude-code --project $project --open-web-dashboard false
claude mcp get serena
```

若 `serena` 已存在，先用 `claude mcp get serena` 核對，別重複加同名伺服器。
local 設定雖作用於本專案，實際存放在使用者的 `~/.claude.json` 專案項目中，不是 `.mcp.json`。
重新啟動 Claude Code，用 `/mcp` 檢查。若啟動真的超時，可在啟動 Claude 的終端機設定
`$env:MCP_TIMEOUT = '120000'` 後重開，不要在每個 task 無限重試。
[Claude 的作用範圍](https://code.claude.com/docs/en/mcp#mcp-installation-scopes)、
[Serena 客戶端接線](https://oraios.github.io/serena/02-usage/030_clients.html#claude-code)

初期不要執行會一次改動更多設定的 `serena setup claude-code`，也不要替換 Claude 的整份
system prompt 或新增「不用 Serena 就阻擋」的 Hook；先保留必要的原生讀取／搜尋回退。

## Codex CLI 與 App 設定

使用**專案層級** `.codex/config.toml`，避免把這個 repo 的絕對路徑掛到所有專案。
官方支援可信任專案的本機 MCP 設定；同一 host 的 CLI 與 App 共用設定來源。
[OpenAI 官方 MCP 文件](https://learn.chatgpt.com/docs/extend/mcp)

以下只產生要貼上的區塊，不會覆蓋既有設定：

```powershell
$projectJson = (Get-Location).Path | ConvertTo-Json -Compress
$commandJson = (Get-Command serena -CommandType Application).Source | ConvertTo-Json -Compress
# 用 JSON 的引號與跳脫產生 TOML 字串，避免 Windows 反斜線變成跳脫序列。
@"
[mcp_servers.serena]
command = $commandJson
args = ["start-mcp-server", "--context", "codex", "--project", $projectJson, "--open-web-dashboard", "false"]
startup_timeout_sec = 120
tool_timeout_sec = 120
"@
```

若不存在 `.codex/`，建立該目錄及 `config.toml`；若已有設定，**只合併一個
`[mcp_servers.serena]` 區塊**，不要覆蓋整檔或建立重複表格。本檔已列入 Git 忽略。
不另外跑會寫使用者全域設定的 `codex mcp add` 或 `serena setup codex`。

在你信任本專案的前提下重新開啟 task。CLI 可用 `codex mcp list` 及互動 `/mcp` 檢查；
App 另核對 MCP 面板及 task 的實際工具。不要因為客戶端選單不同就新增第二個全域伺服器。
兩個客戶端各自啟動本機 Serena 程序是正常的；專案設定相同不代表程序共用。

## 使用：先概覽，再取符號

在 Claude Code 與 Codex 各做一次以下唯讀測試：

> 先讀本專案規範。確認 Serena 的作用專案是 D:\GitProject\SqlAssist.Ssms22。
> 用 Serena 取得 src/SqlAssist.Core/Parsing/SqlTrivia.cs 的符號概覽。
> 再定位 SqlTrivia 的 Skip 方法，先不含本體，確認後才讀這個方法本體。
> 查出引用 Skip 的符號，只回傳前五個檔案與行號，清楚註明不是完整清單。
> 不讀全庫、不建立新記憶、不修改檔案。

通常會用到 `get_symbols_overview`、`find_symbol`、`find_referencing_symbols`。
實際引數依當前 MCP schema，不把自然語言問句直接當符號名稱。
如果目前連線尚未選專案或要求讀初始化指令，先完成那一步，再檢索。
不要把完整 Serena 提示、索引或工具清單貼回 CLAUDE.md。

## 測試與排錯

1. `serena --version` 為 `1.7.0`；`Get-Command serena` 與 `dotnet --list-sdks` 符合預期。
2. `project index`、`project health-check` 結束碼皆為 0，不能只看日誌中曾出現成功字樣。
3. 兩個客戶端都能**實際**呼叫符號工具並返回方法及來源路徑，不是退回 Shell 後口頭宣稱成功。
4. 用 `rg -n -F 'SqlTrivia.Skip' src tests` 做獨立交叉檢查；文字搜尋與語意引用定義不同，
   用來找漏項線索，不要求兩者筆數必須相同。檢查 `git diff`，唯讀測試不應改產品來源。
5. 對 Core、Metadata、Ssms22 各選一個代表性符號重做。Core 能查不代表 SSMS 平台都能查。

- **找不到 serena／pwsh**：確認是在同一 host，完整重開 App；Codex／Claude 的 executable
  採安裝後實際路徑，Roslyn 額外需要 `pwsh` 位於該程序的 PATH。
- **.NET／方案載入失敗**：確認本專案要求的 SDK 與 SSMS 相依，按 [開發文件](development.md)
  建置並讀失敗紀錄；不要為了語言服務改目標框架、取消警告或改回 VSTest。
- **UnicodeEncodeError（cp950）**：若 `health-check` 因輸出 Emoji（`\u2705`）出現編碼例外，
  確認已執行 `[Environment]::SetEnvironmentVariable("PYTHONUTF8", "1", "User")`
  或在當前 session 設定 `$env:PYTHONUTF8 = '1'`。
- **引用缺漏**：保留原生搜尋、原始碼與測試驗證。MEF、事件、反射與 SQL 字串並非都能靠
  Roslyn 證明完整；空結果不是沒有引用。
- **設定與快取**：調整專案設定後重新啟動 MCP，來源改動則以實際回傳及需要的索引更新驗證；
  不要要求 AI 每次開 task 都做全庫 onboarding。
- **停用**：Claude 用 `claude mcp remove --scope local serena`；Codex 在專案的
  `[mcp_servers.serena]` 加 `enabled = false` 後重開。需要解除安裝才用 `uv tool uninstall serena-agent`。
  不刪除整份使用者設定，也不影響原生建置、測試或共用節流工具。
