# 翻譯術語表

本頁包含 zh-Hant → en 的固定譯名與英文文風，是翻譯 `.resjson` 時唯一要讀的文件；
檔案格式與遷移流程見[在地化](localization.md)。

## 文風

- 對齊 SSMS／Visual Studio 英文介面的用詞；拿不準時照 Microsoft 術語，不自創。
- 選單命令照 Visual Studio 選單用 Title Case（`Go To Definition`）；其餘標題、按鈕、設定項目用
  sentence case（`Copy definition`），與 SSMS 22 的設定頁一致。
- 按鈕與命令用祈使動詞開頭；狀態句用完整句子並以句點結尾，中文沒有句號的短標籤英文也不加。
- 全形標點改成英文標點：`，`→`, `、`：`→`: `、`「」`→`""`、`（）`→` ()`、`…`保留。
- T-SQL 關鍵字維持大寫（`SELECT *`、`INSERT INTO`）；產品名稱不翻（SqlAssist、SQL Memory、SQL Search）。
- 佔位符 `{name}` 原樣保留，可以調整位置；英文單複數由句型避開（`Columns: {count}`），
  避不開就回報，另拆兩個鍵，不在程式裡拼字尾。
- 英文通常比中文長，按鈕與欄位標題能短就短。

## 固定譯名

| zh-Hant | en |
|---|---|
| 建議清單 | suggestion list |
| 程式碼片段／片段 | code snippet／snippet |
| 萬用字元 | wildcard |
| 展開 | expand |
| 結構預覽 | structure preview |
| 參數提示 | parameter info |
| 滑鼠停留提示 | Quick Info |
| 區塊配對 | block matching |
| 自動配對 | auto-pairing |
| 結構描述 | schema |
| 資料表／檢視／預存程序 | table／view／stored procedure |
| 資料表值函式／純量函式 | table-valued function／scalar function |
| 資料行、欄位 | column |
| 衍生資料表 | derived table |
| 暫存資料表／資料表變數 | temporary table／table variable |
| 同義字 | synonym |
| 定序 | collation |
| 連結伺服器 | linked server |
| 查詢視窗 | query window |
| 物件總管 | Object Explorer |
| 結果格線 | results grid |
| 移至定義 | Go to Definition |
| 指令碼 | script |
| 結構健檢 | schema check |
| 收藏 | favorite（清單名 Favorites） |
| 草稿 | draft |
| 執行紀錄 | execution history |
| 未存檔內容（當機還原） | unsaved content (crash recovery) |
| 版本（收藏的歷史） | revision |
| 版本（產品） | version |
| 保留、清理 | retention、cleanup |
| 通知 | notification |
| 設定 | Settings（SSMS 22 的設定視窗名稱） |
| 診斷紀錄 | diagnostic log |
| 伺服器／資料庫／連線 | server／database／connection |
