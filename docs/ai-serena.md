# Serena：選用的 C# 符號搜尋

**Claude Code 用外掛；Codex 用本機 MCP。兩者不需要共用一套安裝腳本。**
沒裝也可用原生讀檔、搜尋與測試。日常先看 [AI 工作流程](ai-workflow.md)，返回 [索引](index.md)。

## Claude Code：直接安裝外掛

在 Claude Code 輸入：

```text
/plugin install serena@claude-plugins-official
```

也可用 `/plugin` 的 Discover 搜尋 Serena，安裝至 User scope，讓其他專案也能使用。
已看到 `plugin:serena:serena` 且啟用，就不必重裝。依安裝結果重開 Claude，再用 `/mcp` 檢查。
若外掛提示缺少 uv／語言服務依賴，照外掛說明補裝；不用再手動登記另一份 Serena。
[Claude 官方外掛說明](https://code.claude.com/docs/en/discover-plugins)

**不要再執行 `claude mcp add serena` 或建立手動 Serena 的 `.mcp.json`。**
若舊設備仍有名為 `serena` 的 Local／Project MCP，確認來源後只移除舊項目，
保留 `plugin:serena:serena` 及其他伺服器。[MCP 作用範圍](https://code.claude.com/docs/en/mcp#mcp-installation-scopes)

## Codex：安裝獨立 CLI

目前已可用的設備不用重做。新設備需要 PowerShell 7、uv 與 .NET 10+；
這是 C# 語言服務的需求，**不改產品的 netstandard2.0／net48**。

```powershell
# 只補尚未安裝的工具；不要把 AI 工具依賴加進產品專案。
winget install --exact --id Microsoft.PowerShell
winget install --exact --id astral-sh.uv
```

重開 PowerShell 7，確認 `dotnet --list-sdks` 有所需 SDK，再安裝固定版本：

```powershell
uv tool install --python 3.13 'serena-agent==1.7.0'
uv tool update-shell
```

再重開終端機，確認 `serena --version` 為 `1.7.0`。
[Serena 安裝](https://oraios.github.io/serena/02-usage/010_installation.html)、
[C# 需求](https://github.com/oraios/serena/blob/v1.7.0/docs/01-about/020_programming-languages.md)

## Codex：只設定這個專案

已存在且可用的本機設定直接沿用。新設備要啟用時，建立／編輯專案根目錄的
`.codex/config.toml`，只加入下面的 Serena 區塊；**同名區塊已存在就修改，不覆蓋其他設定。**

`command` 須能從 Codex 的 PATH 找到；否則用 `Get-Command serena` 查到的完整路徑。
**下面的專案路徑務必換成這台設備的實際 clone 位置。**

```toml
[mcp_servers.serena]
command = 'serena'
# App 的啟動目錄不一定是專案根目錄，所以明確指定專案路徑。
args = ['start-mcp-server', '--context', 'codex', '--project', 'D:/GitProject/SqlAssist.Ssms22', '--open-web-dashboard', 'false']
startup_timeout_sec = 120
tool_timeout_sec = 120

[mcp_servers.serena.env]
# 只影響 MCP 子程序，避免 Windows 預設編碼造成診斷輸出錯誤。
PYTHONUTF8 = '1'
```

此檔不進 Git；以上範例就是換設備時的設定參考，沒有另一份模板或產生器。
在信任本專案的前提下重開 Codex，用 MCP 面板或 `/mcp` 檢查；
CLI 也可用 `codex mcp list`。不要再建立指向同一專案的全域重複登記。
[OpenAI 官方 MCP 文件](https://learn.chatgpt.com/docs/extend/mcp)

## 兩者共用的專案規則

[.serena/project.yml](../.serena/project.yml) 隨 Git 共用：C#、UTF-8／LF、唯讀、
忽略 Git 排除的檔案，以及按需查符號。不再跑 `serena project create` 覆蓋它。
個人差異放 `.serena/project.local.yml`，快取／紀錄留本機。
`read_only` 只限制 Serena 的編輯工具，不禁止 AI 用原生編輯工具修改程式。
[Serena 設定說明](https://oraios.github.io/serena/02-usage/050_configuration.html)

## 使用與測試

在 Claude／Codex 各做一次：

> 先遵守專案規範，確認 Serena 已啟用目前這份版本庫的實際路徑。
> 查 SqlTrivia.cs 的符號概覽，再讀 SqlTrivia.Skip 方法與它的引用。
> 只列必要片段與來源行號；不讀全庫、不新增記憶、不修改檔案。

- 看到「已連線」不等於 C# 可用；必須實際拿到正確方法及來源路徑。
- 用原生搜尋交叉檢查；引用查不到不代表沒有引用，MEF／反射等尤其不能只靠索引。
- 不用每次開 task 都跑 onboarding 或重建全庫。
- 要排查獨立 CLI 時，才用 `serena project health-check .`；輸出很多可套用節流助手。
  若有 `FindReferencingSymbolsTool failed`，即使結束碼為 0，也不能宣稱所有符號功能正常。
- Windows CLI 出現編碼錯誤，可在當前終端機設 `$env:PYTHONUTF8 = '1'`，不必改整台設備。
- 停用：Claude 用 `/plugin` 停用 Serena 外掛；Codex 在本機 Serena 區塊加
  `enabled = false`。**不要把 RTK 或輸出節流器套在 MCP 的 stdio 指令外面。**
