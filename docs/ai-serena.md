# Serena：選用的 C# 符號搜尋

**Claude Code 與 Codex 都用同一支本機 CLI，各自只加一份專案設定。**
沒裝也可用原生讀檔、搜尋與測試。日常先看 [AI 工作流程](ai-workflow.md)，返回 [索引](index.md)。

## 安裝本機 CLI（兩端共用）

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

再重開終端機，確認 `serena --version` 為 `1.7.0`，並用 `Get-Command serena` 記下完整路徑。
[Serena 安裝](https://oraios.github.io/serena/02-usage/010_installation.html)、
[C# 需求](https://github.com/oraios/serena/blob/v1.7.0/docs/01-about/020_programming-languages.md)

**不要裝 `serena@claude-plugins-official` 外掛。**它用 `uvx --from git+…` 每次啟動都去
解析 GitHub，版本不固定、冷啟動會超過預設 30 秒逾時，而且不帶 `--context` 與
`--open-web-dashboard false`——每個交談都會另開一個儀表板分頁，並端出 Claude Code
本來就有的讀檔／編輯／執行工具。已經裝了就用 `claude plugin uninstall serena@claude-plugins-official`
移除，再重開 Claude Code。

## Claude Code：只設定這個專案

建立／編輯專案根目錄的 `.mcp.json`（已被 `.gitignore` 忽略，不進版控）：

```json
{
  "mcpServers": {
    "serena": {
      "command": "C:/Users/<你>/.local/bin/serena.exe",
      "args": [
        "start-mcp-server",
        "--context", "claude-code",
        "--project", "D:/GitProject/SqlAssist.Ssms22",
        "--open-web-dashboard", "false"
      ]
    }
  }
}
```

**路徑務必換成這台設備的實際位置**，`command` 用 `Get-Command serena` 查到的完整路徑。
重開 Claude Code 後首次會詢問是否信任這台 server；允許後 `.claude/settings.local.json`
的 `enabledMcpjsonServers` 會記下 `serena`。用 `/mcp` 確認狀態。

**不要用 `claude mcp add --scope local`。**它以專案路徑當索引鍵寫進 `~/.claude.json`，
而同一個資料夾可能同時存在 `D:\GitProject\…` 與 `D:/GitProject/…` 兩筆條目，
斜線方向不同就讀不到，看起來像設定憑空消失。`.mcp.json` 放在專案內沒有這個問題。

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

## 儀表板與並存

MCP 是 stdio、每個交談各起一個 Serena 程序，這是常態；Claude 與 Codex 同時開就會有兩組。
上面的 `--open-web-dashboard false` 只擋自動開分頁。要連本機的
`~/.serena/serena_config.yml` 一起關，把 `web_dashboard_open_on_launch` 設為 `false`；
需要時再手動開 http://localhost:24282/dashboard/ 。此檔是每台設備一份，不進版控。

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
- 停用：Claude 把 `.claude/settings.local.json` 的 `enabledMcpjsonServers` 清空，
  或移掉 `.mcp.json` 的 serena 區塊；Codex 在本機 Serena 區塊加 `enabled = false`。
  **不要把 RTK 或輸出節流器套在 MCP 的 stdio 指令外面。**
