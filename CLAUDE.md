# SqlAssist for SSMS 22

SSMS 22.9.x 的 T-SQL 擴充：`SqlAssist.Core`（netstandard2.0，零 VS 相依）、
`SqlAssist.Metadata`（netstandard2.0，只依賴 `System.Data`）、`SqlAssist.Ssms22`（net48 VSIX）。

## 開始工作

Claude Code 讀本檔；Codex 經 `AGENTS.md` 讀本檔。同一套規則，不複製第二份。
本檔只放通用硬規則。**先讀 [docs/index.md](docs/index.md)，再讀變更範圍對應的護欄章節
與功能文件，才能動手。** 功能禁令只是移到按需文件，沒有取消；跨範圍修改必須合併閱讀。
不清楚位置時才查索引指向的詳細路徑表，不要先讀所有文件或建立全專案摘要。

## 分層

- **禁止**讓 `SqlAssist.Core` 或 `SqlAssist.Metadata` 參照 Visual Studio／SSMS 的組件。
- **禁止**把只看文字就能判斷的邏輯寫進 `SqlAssist.Ssms22`——那裡跑不了單元測試。
  Ssms22 只做接線：拿服務、掛事件、把結果寫回編輯器。
- **禁止**在 `Core/Matching` 參照 `Core/Completion`。Matching 是與領域無關的字串比對。

## 資料夾與命名

- **禁止**資料夾與命名空間不一致。`src/X/Foo/` 一律是 `X.Foo`，測試專案鏡像同一份路徑。
- **禁止**為單一檔案開資料夾。
- **禁止**用相對命名空間限定（`Metadata.SqlObjectInfo`）；一律 `using` 加簡名。
- **禁止**手動編輯 `Keywords/SqlKeywordCatalog.Generated.cs`。改
  `tools/Generate-Keywords.ps1` 後重跑，產物要進版控。
- **禁止**把真實系統的資料表、欄位、預存程序或結構描述名稱寫進測試、註解與文件。
  這個 repo 是公開的，識別字本身就是使用者的私有資產。測試資料一律取自同一個
  虛構的圖書館領域：`Lib_Reader`／`Lib_Tag`（讀者與標籤，`libr`、`lr` 是它的縮寫
  比對案例）、`PUBLISHER`／`PUBL_CODE`（全大寫底線風格）、`Cat_BookCopy`／`CopyNo`
  （前綴加 PascalCase）、`Loan`／`LoanDetail`／`Copy`／`Branch`。需要新名字時沿用
  這個領域，不要另起爐灶——換一套就等於再開一次「哪些字算安全」的判斷。
  例外只有 T-SQL 本身的保留字案例（`Order`、`User`）與產品內建的片段捷徑（`ssf`），
  那些是語言與產品事實，不是誰的 schema。

## 程式碼與建置

- **禁止**留下編譯警告（`TreatWarningsAsErrors`），也**禁止**關掉 `Nullable`。
- **禁止**改回 VSTest 轉接層。`global.json` 已把執行器指定為 Microsoft.Testing.Platform，
  跑測試用 `tools\Run-CoreTests.ps1` 或 `dotnet test <方案>`。
- **禁止**寫「這行在做什麼」的註解。註解只寫**為什麼**：這樣選的理由、
  試過而失敗的做法、以及不這樣寫會出現的症狀。現有檔案就是範本。
- **禁止**用非繁體中文撰寫註解與文件。
- **禁止**在工具腳本裡寫死 SSMS 路徑或擴充的 Identity Id；
  一律從 `tools/SqlAssist.Tools.psm1` 取，並支援 `-SsmsInstallDir` 覆寫。
- **禁止**為了「可設定」而加設定。要不要加由功能自己評估：不用時沒有成本的
  東西不必加開關，加了也沒有人會動；真要加照索引的設定護欄。

## 文字檔格式

- 所有文字檔一律使用 **UTF-8（無 BOM）與 LF**；唯一例外是 `.sln`，Visual Studio
  只認 BOM 才當 UTF-8。唯一規格是根目錄的 `.editorconfig` 與 `.gitattributes`，
  `core.autocrlf` 不得覆蓋它。
- **禁止**在工作結束前批次「還原 CRLF」或補回 BOM。產生與修改時直接保留 LF，並以
  `tools/Check-TextFiles.ps1` 驗證；只為換行重寫整份檔案會製造無意義差異與 token 成本。
- 原始診斷串流是例外：只存放在被忽略的 `artifacts/`，保留原編碼與換行，不得提交。

## 按需讀取與輸出節流

- 預設先查檔名、符號與標題，再讀命中區段；搜尋限相關目錄。除非正在排查產物，
  不掃 `bin/`、`obj/`、`.vs/`、快取與完整紀錄。不把生成檔或全專案打包灌入上下文。
- **禁止**整檔讀超過 8000 字元的文件。長檔用 `tools/Read-Context.ps1` 按行讀取
  （預設 80 行、最多約 6000 字元），依續讀提示補足；命中片段不是完整證據。
- 建置、測試與其他高輸出非互動命令，優先透過 `tools/Invoke-QuietCommand.ps1`。
  它保留完整 stdout／stderr 與結束碼，只顯示有上限的尾段；失敗先定位紀錄再讀取。
  **禁止**用截斷、只比對「成功」字樣、略過測試或吞掉結束碼來換取省 token。
- RTK 與 Serena 都是選用工具。需要它們才由索引讀教學；未安裝、未連線或未命中時
  回退原生命令。不要把它們的完整手冊、生成記憶或工具清單加入本檔。
- RTK 可用時當成大輸出的取樣器：`git`／`find`／`tree`／`grep` 上百行以上能省八成，
  小輸出反而變長。Hook 無條件生效，所以規則寫在這裡而不是教學：**禁止**拿它的輸出
  當完整證據——實測 diff 2011→552 行、grep 1441→204 行，雖標了 `truncated`／
  `+N more` 也留了完整紀錄，漏看就是只審查了一小部分。審查差異與窮盡搜尋
  一律 `rtk proxy <原命令>`。
  Serena 已連線時先查符號概覽，再讀必要符號本體；不為節省 token 重複安裝或重建全庫。
- 回覆只列變更、驗證與未解風險，不重貼完整程式碼或成功紀錄。
  不相關工作另開 task；同一問題保留必要上下文。快取折扣不等於上下文消失，
  字元預算也不等於精確 token／訂閱額度。

```powershell
# 代理與人工使用同一套驗證，不為了縮短輸出另寫一份測試流程。
pwsh -NoProfile -File tools/Invoke-QuietCommand.ps1 -ScriptPath tools/Run-CoreTests.ps1
pwsh -NoProfile -File tools/Invoke-QuietCommand.ps1 -ScriptPath tools/Build-Extension.ps1
```

## 文件維護

- `CLAUDE.md` 上限 3500 字元、`AGENTS.md` 上限 800 字元、索引上限 4000 字元，
  其他 `docs/` 文件上限 14000 字元；由 `tools/Check-Docs.ps1` 守住，超過就按主題拆分。
- **禁止**在本檔列舉 `docs/` 檔名。路由只有 [docs/index.md](docs/index.md) 一份。
- **禁止**把細節寫回 `README.md`；它只做入口與索引，內容進 `docs/`。
- **禁止**改完文件不跑 `tools/Check-Docs.ps1`；文字格式另跑 `tools/Check-TextFiles.ps1`。
  修改讀取／節流腳本時，另跑 `tools/Test-AgentWorkflow.ps1`，不可只測成功路徑。
