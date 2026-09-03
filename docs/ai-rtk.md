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

## 實測壓縮率，以及為什麼帳面收益仍然很低

壓縮率與輸出大小成正比（本 repo 實測）：`tree src` 2164→213 位元組、
`find src -name "*.cs"` 12973→1407、`grep -rn public src` 1441→204 行，都是省八九成；
但 `ls src/SqlAssist.Core` 169→196，小輸出因為多了統計行而淨虧。

即使如此，累計 `rtk gain` 長期停在 1% 以下。原因不是 RTK 失效，而是 **Hook 只攔 Bash**：
Claude Code 的 Grep／Glob／Read 是獨立工具，不經過 Bash，日常搜尋與讀檔根本碰不到 RTK，
真正流經 Bash 的幾乎只剩 Git。要拿到上表的收益，得刻意用 Bash 跑 `find`／`tree`／`grep`，
而那通常不如直接用內建工具。**先看 `rtk gain` 再決定要不要留，不要憑壓縮率想像收益。**

## 日常使用

```powershell
# 狀態查詢只需摘要；審查差異則必須保留完整內容。
rtk git status
rtk git log
rtk proxy git diff --no-ext-diff
```

- 未安裝就直接用 `git`、`rg` 等原生命令，不能仍硬加 `rtk` 前綴。
- 完整搜尋、必要護欄及原始錯誤不能用有損摘要代替。
- 本專案建置、Microsoft.Testing.Platform 測試仍用 [輸出節流助手](ai-workflow.md#工具輸出節流)，
  不改成 `rtk test`／`rtk err`。Hook 也不代表 Claude 內建 Read／Grep 全部會被改寫。

## 測試與排錯

1. 比較原生 `git status --short` 與 `rtk git status`，檔案狀態應一致。
2. Claude／Codex 各開新 task，要求用 RTK 查 Git 狀態；檢查實際工具紀錄及 `rtk gain`。
3. 驗證 proxy 不吞掉失敗：

```powershell
rtk proxy pwsh -NoProfile -Command 'exit 23'
if ($LASTEXITCODE -ne 23) { throw '結束碼未保留。' }
```

終端機可用、App 不行時，先完整重開 App 並核對其 PATH，不重複安裝。
Hook 過濾有疑慮時用 `rtk proxy` 繞過；停用時只調整 RTK 那一項，不關掉所有 Hook。
`rtk gain` 是命令輸出的近似統計，不是整個 task 的訂閱額度或 API 帳單。
