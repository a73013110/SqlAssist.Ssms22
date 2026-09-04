# 片段自訂、合併與存檔

## 內建值與使用者 override

內建定義只有一份：
`src/SqlAssist.Core/Snippets/DefaultSnippets.json`，以 Embedded Resource 隨 VSIX 發布。
不要把 45 筆內容寫進 C#，也不要放進 VSIX 安裝步驟複製到使用者目錄。

使用者檔位於 `%APPDATA%\SqlAssist\snippets.json`，v2 只存：

- 修改過的內建項目；
- `{ "id": "builtin...", "disabled": true }` 的停用紀錄；
- 使用者新增的完整項目。

檔案不存在代表「完全使用內建值」，不會在第一次啟動時建檔。這讓新版 VSIX 可以直接
更新未自訂的內建片段。管理介面會標示「已自訂」與「已停用」，並提供「還原此預設」；
全部還原後寫出的 override 清單是空的。

```json
{
  "version": 2,
  "snippets": [
    {
      "id": "builtin.ctb",
      "category": "ddl",
      "shortcut": "ctb",
      "title": "CREATE TABLE",
      "description": "建立資料表",
      "expansionMode": "tabStops",
      "positions": ["StatementStart", "BlockStart"],
      "code": "CREATE TABLE $schema$.$table$\n(\n    $column$ $dataType$ NOT NULL\n)$end$;",
      "placeholders": [
        { "id": "schema", "default": "dbo", "tooltip": "結構描述" },
        { "id": "table", "default": "TableName", "tooltip": "資料表名稱" },
        { "id": "column", "default": "ColumnName", "tooltip": "欄位名稱" },
        { "id": "dataType", "default": "INT", "tooltip": "資料型別" }
      ]
    },
    { "id": "builtin.dt", "disabled": true }
  ]
}
```

`category` 是固定集合：`select`、`dml`、`ddl`、`controlFlow`、`clause`、`other`；
不認得的值落到 `other`。`positions` 重用 `SqlKeywordPosition`，缺席為 `Any`。

**`positions` 給得太緊的症狀是全靜默的**：使用者只覺得「這個片段有時候有、
有時候沒有」。語句級片段一律要同時給 `StatementStart` 與 `BlockStart`——分析器在
`BEGIN` 之後只回報 `BlockStart`，只給前者的話整批片段在 `BEGIN…END` 區塊裡會消失。
守門的是 `SqlSnippetDefaultsTests.內建片段在它自然的位置找得到`；新增片段時
要在那份表格加一行。

`minimumSqlServerVersion` 不存在：產品下限已固定，為它查詢每條連線的版本只會把資料庫 I/O
帶進按鍵路徑。

## 遷移、相容與存檔

- v1 是完整清單。第一次讀到時，先把原檔備份成
  `%APPDATA%\SqlAssist\snippets.v1.backup.json`（只寫一次），再與不可修改的 v1
  三筆凍結快照比較，轉成最小 v2 override。
- v1 自訂捷徑若在 v2 成為內建捷徑，會轉成該內建 ID 的 override，不產生兩筆撞名項目。
- `version > 2` 時可以讀已知欄位，但整份進入唯讀模式，避免舊版把新欄位覆蓋掉。
- v2 保留頂層 `snippets` 鍵並只新增欄位；降回舊版時，舊讀取器至少仍看得到完整 override
  與自訂項目。
- 存檔先寫同目錄暫存檔，再用 `File.Replace` 原子置換；目標不存在時才用 `File.Move`。
- 允許 JSON 註解與尾隨逗號。整份語法壞掉時保留原檔、切成唯讀、顯示錯誤，並繼續提供內建片段。

不使用檔案監看器：清單只在第一次使用時載入並維持穩定參考，管理介面成功存檔才換快照。
因此按鍵路徑沒有磁碟 I/O，也不會因每次 `Current` 產生新物件而重建整批建議。直接用文字
編輯器修改 JSON 後，需要重新啟動 SSMS 才會載入。

這是 SqlAssist 自己的格式，與 SSMS「程式碼片段管理員」的 `.snippet` 檔不互相註冊；
只有提交時把選到的項目轉成記憶體中的原生 XML。
