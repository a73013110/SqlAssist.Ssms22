# 在地化

本頁包含介面文字的來源、哪些不翻、語言切換與遷移流程；固定譯名與英文文風見
[術語表](localization-glossary.md)。

## 文字從哪裡來

- 使用者看得到的文字一律寫在 `<類別>.<語言>.resjson`，與用它的程式碼放同一個資料夾。
  `zh-Hant` 是來源語言，決定有哪些鍵與佔位符；檔案是扁平的「鍵 → 文字」，可寫 `//` 註解給譯者。
- `tools/SqlAssist.TextGenerator` 產生同名的 public static class，命名空間由 `src/<專案>/<資料夾>`
  推出。沒有佔位符的是屬性，有的是方法：`UpdateText.Available(latest, current)`。
- 佔位符一律具名（`{count}`、`{size:N0}`），譯文可以調換語序；不收 `{0}`。英文單複數用句型避開，
  避不開就拆兩個鍵，不在程式裡拼字尾。
- 各語言的鍵、佔位符不一致或缺檔都是建置錯誤（SQLTXT001–010），不在執行期退回來源語言。
- 不用 resx：衛星組件要靠 VSIX 探測路徑與隔離 AppDomain 各自載入，載不到只會安靜退回；
  產生器把所有語言編進同一個組件，參數個數也由編譯器檢查。XML 對 diff 與 AI 也都貴。
- 唯一例外是設定頁：註冊檔只能寫 `@鍵;{packageGuid}`，由 SSMS 到套件組件的資源查表。文字照樣寫在
  `Ssms22/Settings/SettingsPageText.<語言>.resjson`、照同一流程翻譯，但不產生類別；`SettingsPageText.targets`
  在建置時把英文編成中性資源（其他介面語言都退回它）、其餘語言編成衛星組件，鍵不齊或引用不存在的鍵是
  SQLSET001–006。衛星組件在 Deploy 白名單裡，缺了只會安靜退回英文。
- 語言清單只有根目錄 `Directory.Build.props` 的 `SqlAssistTextLanguages` 一份（逗號分隔）。
  新增語言：加在那裡、每份 `.resjson` 與資料型覆蓋檔補一份、語言設定加一個列舉值。
- 跨功能的共用詞（確定、取消、複製）放 `Core/Localization/CommonText`，不在各功能各翻一次。

## 資料型文字

- `BuiltInDocs.json`、`DefaultSnippets.json` 這種一筆多欄、帶巢狀表的內嵌資源不進產生器：來源維持繁中，
  旁邊放同名覆蓋檔 `<名稱>.<語言>.json`，由 `Core/Localization/SqlTextOverlay` 依語言疊上去，缺的欄位用來源那一句。
- 格式是 `{ "<編號>": { "<欄位>": "譯文" } }`，只放含中文的文字欄；編號與欄位由載入器定，陣列位置從 0 起算：

  | 來源 | 編號 | 欄位 |
  |---|---|---|
  | `BuiltInDocs` 的 `docs` | `name` | `summary`、`example` |
  | `BuiltInDocs` 的 `tables` | `tables.<編號>` | `title`、`columns.<欄>`、`rows.<列>.<欄>` |
  | `DefaultSnippets` | `id` | `title`、`description`、`code`、`placeholders.<欄位 id>.tooltip`／`.default` |

- 範例與片段樣板只翻 `--` 註解與中文字串常值，`$surround$`、`{name}` 原樣保留；`SqlTextOverlayTests` 守編號、欄位與標記。
- 覆蓋檔內嵌時標 `WithCulture="false"`，否則 MSBuild 把 `.en.json` 當文化資源拆進衛星組件。
- 關鍵字目錄（`SqlDataTypeCatalog` 這幾個 `.cs`）的一行說明走 `.resjson` 而不是覆蓋檔：覆蓋檔要把中文來源留成
  字面值（SQLTXT100 擋），`.resjson` 的鍵由編譯器檢查。目錄存 `() => DataTypeText.Int`，取值時才讀語言。
- 提示視窗的截斷上限以字元計、不分語言，英文照同一個上限寫得精簡；`SqlBuiltInDocCatalogTests` 兩種語言都檢查。

## 什麼不翻

- 診斷紀錄、平台防護的作業名稱與診斷報告：給維護者比對，固定繁中。在接收的參數或所在成員
  標 BCL 的 `[Localizable(false)]`（CA1303 同一套語意），SQLTXT100 就不檢查。
- 使用者資料（SQL Memory、收藏、自訂片段）與 SQL Server 回傳的訊息。
- 設定鍵、moniker、列舉字面值與儲存格式：那是資料。
- 已經寫進編輯器的文字不回頭改；之後產生的指令碼註解用當下的語言。

## 目前語言

- 取值走 `Core/Localization/SqlText.Current`。**不動** `Thread.CurrentUICulture`：那是 SSMS 的狀態。
- 「跟隨 SSMS」由 `SqlLanguage.Match` 依宿主介面文化挑：zh-* 用繁中，其餘用英文。
- 設定生效前是來源語言，所以既有測試的中文斷言不必改。固定語言用 `SqlText.Use(...)`
  （AsyncLocal，平行測試互不干擾）；會切換全域語言的測試放進不平行的集合。
- 隔離 AppDomain（SQL Memory 儲存）有自己一份 `SqlText.Current`，不跟著宿主切換。隔離側只回分類、原因碼與
  錯誤碼，例外訊息當診斷（`SqlMemoryStorageException` 的訊息參數標了 `[Localizable(false)]`）；
  給使用者的句子由宿主的 `SqlMemoryTimeText.Describe` 組。

## 即時切換

| 表面 | 切換後 |
|---|---|
| 每次才產生的文字（補全、提示、通知、指令碼） | 下一次就是新語言；含譯文的快取以語言為鍵（`SqlLanguageCache`），或在 `SqlText.Changed` 時清掉 |
| 工具窗（SQL Search、SQL Memory） | `Changed` 時重建內容，保留查詢字串這類輕量狀態 |
| 強制回應對話框 | 開著時進不了設定，不處理 |
| 選單命令 | 命令表標 `TextChanges`，在 QueryStatus 設 `Text` |
| 設定頁、vsixmanifest | 只能用 `@key;{packageGuid}` 資源跟隨 SSMS 介面語言，本設定管不到 |

語言設定最後才註冊：只翻了一半的設定屬於「按了只生效一部分」，[設定護欄](rules-settings.md)禁止。

## 遷移一個資料夾（省 token 的做法）

1. 從該資料夾 `.editorconfig` 的清單拿掉要處理的檔名；清空就刪檔。
2. 建置那個專案。SQLTXT100 列出的檔案、行號與前 24 字就是待辦，只讀命中行附近，不必整檔讀。
   Core、Metadata、Sqlite 用 `dotnet build src/<專案> -c Release`；Ssms22 走 `tools/Build-Extension.ps1`。
3. 文字寫進 `<類別>.zh-Hant.resjson`（鍵用 PascalCase），呼叫端改用產生的成員，拼接字串改成
   一整句具名佔位符；只進紀錄的改標 `[Localizable(false)]`。
4. 翻譯交給 `.claude/agents/resjson-translator.md`（Haiku，規則寫在定義裡）：一個區塊只呼叫一次，
   只傳全部 zh-Hant 檔路徑；回來只審譯文。沒有這個代理的工具照術語表手動翻。
5. 建置＋測試；每個區塊至少補一個 `SqlText.Use` 英文斷言。

## 尚未確認

- 選單 `TextChanges` 在 SSMS 22 是否能即時改字。
- 通知的 `Message` 由各功能在建立時組好，換語言後已顯示的那幾列仍是舊語言；通知測試說明裡引用的設定名稱
  要等設定頁有英文名稱後再對一次。
- 補全與編輯器的譯文快取要在即時切換時處理：`Ssms22/Completion/SqlCompletionFilters` 的篩選鈕在型別初始化時就定了字
  （平台以實體比對選取狀態，不能每次新建，要依語言各留一組）；`Ssms22/Blocks/BlockContextHint` 只在區塊或行號變了才重組
  提示（清掉 `_shownPair`）；區塊符號與摺疊提示的 Tag 要等緩衝區變更才重建。
- SQL Memory 未命名視窗的預設文件名稱照擷取當下的語言存進 `Documents.DisplayName`，暫不改存代碼：SSMS 的查詢視窗
  一定帶標題，這條幾乎走不到；改存代碼要動讀取端、名稱搜尋與排序。其餘存進 SQLite 的文字都是使用者資料，不翻。
- 共用 UI 元件在建立當下取字：`Ssms22/UI/SqlIcons` 的圖示朗讀名稱在型別初始化時就定了（`ImageElement` 不可變），
  字型和色彩的分類名稱（`BlockEndpointFormat` 這幾個 `ClassificationFormatDefinition`）在 MEF 建立時定字，
  `SqlAssistChrome.Selection` 的勾選欄朗讀格式在樣板建立時定字；前者要依語言各留一組，其餘隨工具窗重建或重新啟動才換。
- `Ssms22/Snippets/SqlSnippetStore` 的合併結果在載入當下定了內建片段的語言，即時切換要重新合併；存檔端已不會把
  舊語言的內建值誤寫成自訂。
- 提示視窗截斷以字元計：中文 60 字約是英文 60 字的兩倍寬。改成依顯示寬度截斷會連使用者的擴充屬性說明一起放寬，待定。
