# RTK：Windows、Claude Code 與 Codex

範圍：RTK 的安裝、最小設定、使用、測試與停用。返回 [索引](index.md)；
不裝 RTK 仍可使用 [共用工作流程](ai-workflow.md)。
核對日期：2026-09-03；本教學固定 [RTK v0.47.0](https://github.com/rtk-ai/rtk/releases/tag/v0.47.0)。
不使用 crates.io 上另一個同名 `rtk` 套件。

## 安裝一次

1. 從上述官方發行頁下載 **rtk-x86_64-pc-windows-msvc.zip**，適用本專案的 Windows x64。
2. 在 PowerShell 7 驗證 SHA-256，再解壓縮。不要把安裝到 PATH 與雙擊執行檔混為一談。

```powershell
$zip = Join-Path $HOME 'Downloads/rtk-x86_64-pc-windows-msvc.zip'
# 雜湊來自 v0.47.0 官方發行資產；換版本時也必須重新核對，不能略過驗證。
$expected = '26401cf663797bfcfd0d7fbf3acfa5d81a6fb384e8cac188506d69c330d37596'
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ine $expected) {
    throw 'RTK 下載檔雜湊不符，停止安裝。'
}
$installDir = Join-Path $HOME '.local/rtk/v0.47.0'
# 使用新的版本目錄，不以 Force 覆蓋原安裝，方便回退。
Expand-Archive -LiteralPath $zip -DestinationPath $installDir
$executables = @(Get-ChildItem -LiteralPath $installDir -Filter 'rtk.exe' -Recurse -File)
if ($executables.Count -ne 1) { throw '找不到唯一的 rtk.exe，請核對封存內容。' }
$bin = $executables[0].DirectoryName
$env:PATH = "$bin;$env:PATH"
rtk --version
rtk telemetry disable
rtk gain
```

預期版本為 `0.47.0`；`gain` 能顯示統計，初次為零正常。
把 `$bin` 顯示的目錄加入 **Windows 使用者 Path**，再完整重開終端機、Claude Code 與 Codex。
不要使用 `setx PATH ...` 把整份系統／使用者 PATH 蓋掉。
部分搜尋功能需要 `rg`，可先用 `Get-Command rg` 確認；缺少時依
[官方 Windows 安裝說明](https://github.com/rtk-ai/rtk#windows) 補裝 ripgrep。

## 最小設定：兩個客戶端共用

**本專案推薦這一種，沒有 MCP，也不必先裝 Hook。**
安裝後的機器必須讓兩個客戶端都找得到同一個 `rtk.exe`。

| 客戶端 | 怎麼套用 |
|---|---|
| Claude Code | 根目錄 CLAUDE.md 已包含「RTK 可用時用於唯讀 Git 摘要」規則，開新 task 生效 |
| Codex CLI／App | 根目錄 AGENTS.md 已引導讀同一份 CLAUDE.md，不另複製 RTK 手冊 |

不必再執行 `rtk init --codex`：官方模式會加入 AGENTS.md／RTK.md 指令，這裡已有更短的
共用入口。若在別的專案採官方整合，可先看 `rtk init --codex --dry-run` 的變更清單。
Codex 的這個整合是**指令引導**，不是 Claude 的透明 Hook；不要混用 `--codex --hook-only`。
兩種方式的差異可查 [官方支援表](https://github.com/rtk-ai/rtk#supported-ai-tools)。

## Claude Code 自動 Hook：選用

先確認手動命令確實有幫助，才增加這一步。**v0.47.0 的 `--hook-only` 只支援全域
`-g`；不加 `-g` 會警告後不安裝。** 為了只作用於本專案，手動合併到
`.claude/settings.local.json`，這個檔案已被 Git 忽略：

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [{ "type": "command", "command": "rtk hook claude" }]
      }
    ]
  }
}
```

先備份已有的 `.claude/settings.local.json` 到被忽略的 `artifacts/`，**只合併這個 Hook，
不要覆蓋其他設定或重複加入 PreToolUse**。重新啟動 Claude Code。
原生 Hook 命令是 `rtk hook claude`，不需要 Bash／jq 來執行 Hook 本身。
不要同時安裝另一份舊版 `.sh` Hook，也不需要再加入完整 RTK.md。
[旗標與互斥限制](https://github.com/rtk-ai/rtk/blob/v0.47.0/src/main.rs)、
[初始化實作](https://github.com/rtk-ai/rtk/blob/v0.47.0/src/hooks/init.rs)、
[Claude 的專案本機 Hook](https://code.claude.com/docs/en/hooks#hook-locations)。
Claude 內可用 `/hooks` 確認來源為 Local Settings，且命令為 `rtk hook claude`。

Hook 改寫的是 Claude 的 Shell／Bash 工具呼叫；**不代表內建 Read、Grep、Glob 都會被攔截**。
也不要期待所有 PowerShell 複合指令都能正確改寫。對本專案的建置、測試、完整 diff，
優先手動走原生命令或共用節流包裝器；遇到改寫異常，先停用 Hook，不改測試執行器。
初始化詢問遙測時可拒絕，或再次執行 `rtk telemetry disable`。

## 使用

```powershell
# 適合只需要摘要的狀態與歷史查詢。
rtk git status
rtk git log
rtk gain

# 審查與完整性驗證不能用有損摘要代替；proxy 明確繞過 RTK 過濾。
rtk proxy git diff --check
rtk proxy git diff --no-ext-diff
```

兩個客戶端都可使用同一句要求：

> 依本專案共用規則，先確認 rtk 可執行。用 rtk git status 查看摘要；不要修改檔案。
> 若要審查，改讀原始 diff；不要把摘要當成完整差異。

不要用 `rtk read` 壓掉專案護欄或「為什麼」註解；不要把 `rtk grep` 的有限筆數當成
所有引用。Microsoft.Testing.Platform 尚未在本專案完成 RTK 過濾相容性驗證，
不要用 `rtk test`／`rtk err` 替代 [共用測試包裝器](ai-workflow.md#工具輸出節流)。

## 測試：安裝、使用與收益分開驗證

1. **安裝**：`Get-Command rtk` 路徑正確，`rtk --version` 為預期版本，`rtk gain` 可執行。
2. **CLI**：在專案根目錄先跑原生 `git status --short`，再跑 `rtk git status`。
   確認檔案狀態語意一致；乾淨工作樹很短，沒有明顯縮減也正常。
3. **Claude Code**：新 task 執行上面的要求，查看實際命令是否有 `rtk`。
   啟用 Hook 時，再要求跑普通 `git status`，核對工具紀錄與 `rtk gain` 是否新增紀錄。
4. **Codex**：在 CLI 或 App 新 task 做相同測試，確認真正呼叫 `rtk`，不能只接受口頭宣稱。
5. **失敗不被吞掉**：以下明確模擬失敗結束碼，不會修改專案。

```powershell
rtk proxy pwsh -NoProfile -Command 'exit 23'
$code = $LASTEXITCODE
if ($code -ne 23) { throw "結束碼未保留：$code" }
```

上例直接模擬失敗結束碼，不代表已驗證 RTK 每一種過濾器。原生 Git、RTK 摘要與回歸測試
都須各自核對。`rtk gain` 的 token 是近似計數；官方節省百分比只算命令輸出，
不等於整個 task、API 帳單或訂閱額度。[統計範圍](https://github.com/rtk-ai/rtk#how-savings-work)

## 排錯與停用

- 終端機可以、App 不行：完整退出 App 後重開，再在 task 內 `Get-Command rtk`；
  確認是執行 task 的那台機器，而非另一個 WSL／遠端環境。
- 找不到命令：回退原生命令，不讓代理每個 task 都重裝或查完整手冊。
- Hook 有異常：從 `.claude/settings.local.json` 移除本教學加入的 `rtk hook claude`
  項目，保留其他 Hook 與代理規則，再重開 Claude；不刪整個 `.claude/`。
- 不用 CLI 時，移除自己新增的使用者 Path 項目即可；不要刪除其他工具的共用 bin 目錄。
- 升級用新版本目錄、核對新雜湊並重跑上述測試，不直接追逐最新開發分支。
