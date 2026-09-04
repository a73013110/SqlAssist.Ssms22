# 文件維護護欄

修改 `CLAUDE.md`、`AGENTS.md`、README、`docs/` 或 AI 工作流程前必讀。

- 唯一路由是 [index.md](index.md)；`CLAUDE.md` 不列文件清單，也不建立必須逐層點入的導覽頁。
- 依**可獨立修改的主題**拆檔，不按固定行數硬切；路由直接指向葉文件，避免先讀導覽頁。
- 同一規則只留一份。跨頁只放一句連結，不複述理由、表格或程式碼路徑。
- README 只做產品摘要、視覺化功能導覽、安裝與文件入口；實作細節放 `docs/`。精簡時不得
  移除讓 GitHub 訪客直接理解主要功能的代表圖片。
- 預算：`CLAUDE.md` 1000、`AGENTS.md` 400、索引 3500、其餘 Markdown 4000 字元；
  3900 字元即警告。字元只作穩定上限，不宣稱等於模型 token。
- 不整檔讀超過 4000 字元；先查標題，再用 `tools/Read-Context.ps1` 取命中區段。
- 文件完成後執行 `tools/Check-Docs.ps1` 與 `tools/Check-TextFiles.ps1`；若改讀取、節流或
  文件檢查腳本，再執行 `tools/Test-AgentWorkflow.ps1`，成功與失敗路徑都要保留。
