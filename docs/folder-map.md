# 資料夾對應

只有不知道職責落在哪一層時才查；已知符號時直接搜尋。相關文件由[索引](index.md)選取，
本表不重複連結。

`src/X/Foo/` 一律是命名空間 `X.Foo`，`tests/` 鏡像同一份路徑。

## SqlAssist.Core（netstandard2.0，零 VS 相依，可完整單元測試）

| 資料夾 | 職責 |
| --- | --- |
| `Completion/` | 建議項的模型、上下文判斷、篩選與排名 |
| `Keywords/` | 關鍵字、內建函式、全域變數、型別、自動大寫 |
| `Matching/` | 與領域無關的字串模糊比對（**禁止**參照 `Completion/`） |
| `Pairing/` | 輸入分隔字元時要不要補上另一半 |
| `Parsing/` | 詞法分析、註解與括號、範圍與欄位來源解析、識別字括號化 |
| `Preview/` | 浮動預覽的定位、避障、方向遲滯與縮放 |
| `Snippets/` | 片段模型、展開、佔位符與序列化 |
| `Statements/` | INSERT／MERGE／EXEC／函式展開與預留值 |
| `Wildcards/` | `SELECT *` 的判斷與展開後的排版 |
| `Settings/` | 設定 POCO、moniker、數值範圍與讀取 |
| `Diagnostics/` | 版本、健康檢查與匿名診斷摘要 |
| `Json/` | 最小 JSON 讀寫（Snippet 檔與註冊檔測試用） |

## SqlAssist.Metadata（netstandard2.0，只依賴 `System.Data`）

| 資料夾 | 職責 |
| --- | --- |
| `Model/` | 物件、欄位、參數、索引、外來鍵的模型 |
| `Querying/` | 分層的中繼資料查詢與資料列對應 |
| `Caching/` | 依「伺服器＋資料庫」快取，並協調分層載入 |
| `Formatting/` | 型別、欄位呈現與可執行指令碼樣板 |
| `ResultGrid/` | 格線模型、值轉字面值、`#temp` 與 `IN` |

## SqlAssist.Ssms22（net48 VSIX，只做接線）

| 資料夾 | 職責 |
| --- | --- |
| `Completion/` | 非同步 IntelliSense、提交、展開與重開 |
| `Editor/` | 編輯器接線、Tab／Enter、寫回、物件定位與殼層命令 |
| `QuickInfo/` | 滑鼠停留提示 |
| `Preview/` | 浮動結構預覽內容與視窗機制 |
| `Wildcards/` | `SELECT *` 的展開與可展開提示（Tab 由 `Editor/` 分派） |
| `Snippets/` | 片段檔、管理員視窗與 Expansion Session |
| `Settings/` | Unified Settings 讀取、預覽視窗尺寸，以及推給 SSMS 的語言偏好 |
| `Connections/` | 取得 SSMS 查詢視窗的連線，以及另開一個沿用連線的查詢視窗 |
| `Commands/` | 命令識別碼、工具選單與診斷視窗 |
| `ResultGrid/` | 讀取選取範圍並輸出到視窗或剪貼簿 |
| `UI/` | 全擴充共用外觀與佈景筆刷 |
