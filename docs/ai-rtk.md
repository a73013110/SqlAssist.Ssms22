# RTK：選用的命令輸出壓縮

不裝也能開發。日常流程先看 [AI 工作流程](ai-workflow.md)，返回 [索引](index.md)。
本教學固定 [RTK v0.47.0](https://github.com/rtk-ai/rtk/releases/tag/v0.47.0)，適用 Windows x64。
它不是 MCP，也不是 crates.io 上另一個同名套件。

## 安裝一次

1. 從上方官方發行頁下載 **rtk-x86_64-pc-windows-msvc.zip**。
2. 在 PowerShell 驗證下載檔：

```powershell
$zip = Join-Path $HOME 'Downloads/rtk-x86_64-pc-windows-msvc.zip'
# 固定版本與雜湊一起核對，避免安裝錯誤或被替換的檔案。
$expected = '26401cf663797bfcfd0d7fbf3acfa5d81a6fb384e8cac188506d69c330d37596'
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ine $expected) {
    throw 'RTK 下載檔雜湊不符。'
}
```

3. 解壓到自己的工具資料夾，把 **rtk.exe 所在資料夾**加入 Windows「使用者 Path」。
   不要用 `setx PATH` 覆蓋整份環境設定，也不要刪除其他工具共用的資料夾。
4. 完整重開終端機、Claude Code／Codex，確認：

```powershell
rtk --version
rtk gain
# 是否提供遙測與是否省 token 是兩回事，可獨立停用遙測。
rtk telemetry disable
```

版本應為 `0.47.0`；初次 `gain` 沒有統計正常。已安裝且可用就跳過，不必每次 clone 重裝。

## Claude 與 Codex 怎麼使用

| 客戶端 | 設定方式 |
|---|---|
| Claude Code | [.claude/settings.json](../.claude/settings.json) 已提供 Bash Hook；先確認 RTK 存在，沒裝就安靜略過 |
| Codex CLI／App | AGENTS.md 導向共用規則；AI 在 RTK 可用時主動使用，不是透明 Hook |

**這個專案不需要再跑 `rtk init`，也不需要加入完整 RTK.md。**
Claude 可用 `/hooks` 核對 Project Settings。若個人全域／本機設定已有同一個 Hook，
只保留一份，不要刪除其他 Hook 或權限。[Claude Hook 說明](https://code.claude.com/docs/en/hooks)

## 為什麼有 RTK，仍不一定省 token

先前樣本中，大量檔名與搜尋結果曾縮減八九成；但小目錄清單也曾從 169 增為 196
位元組。效果取決於命令與資料形狀，不能只看有沒有 `rtk` 前綴或保證固定節省率。

- `rtk proxy` 的定義是「不過濾，只追蹤用量」；加了前綴不代表內容有壓縮。
- `rtk read` 預設也是 `--level none`。完整讀取不會自動變摘要；先定位與分段才是重點。
- 本專案 Claude Hook 只攔 Bash；獨立 Grep／Glob／Read 及其他工具輸出不會自動經過 RTK。
- `rtk gain` 預設是全域累計的命令輸出估算，不是本次任務或整段對話的實際節省率。
  歷史百分比不能當現況，也不為追求統計數字而重複執行命令。

## 日常使用

選命令的唯一流程見 [AI 工作流程：輸出分工](ai-workflow.md#輸出分工)。不要把「完整審查
不能壓縮」誤解成「探索時也要輸出整份原文」。

本專案建置、Microsoft.Testing.Platform 測試仍走既有腳本，不改成 `rtk test`／`rtk err`。

## 排錯

終端機可用、App 不行時，先完整重開 App 並核對其 PATH，不重複安裝。
Hook 過濾有疑慮時用 `rtk proxy` 繞過；停用時只調整 RTK 那一項，不關掉所有 Hook。
`rtk gain` 是命令輸出的近似統計，不是整個 task 的訂閱額度或 API 帳單。
