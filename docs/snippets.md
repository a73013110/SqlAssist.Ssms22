# 程式碼片段

預設有三個，都可以改：

| 輸入 | 展開結果 | 接續行為 |
|---|---|---|
| `ssf` | `SELECT * FROM ` | 接著只顯示 Table／View |
| `ap` | `ALTER PROCEDURE ` | 接著只顯示 Procedure |
| `af` | `ALTER FUNCTION ` | 接著只顯示 Function |

由 `工具 → SqlAssist → 程式碼片段…` 增刪修，也可以從設定頁的「編輯程式碼片段…」進入。

程式碼裡可以放兩種標記：

- `$名稱$` 是佔位符，展開時換成設定的預設值。佔位符清單由程式碼推導，
  不另外維護——能各自編輯的兩份東西遲早會分岔。
- `$end$` 標示展開後游標要停的位置，標記本身不會留在文字裡。

「展開後立刻再顯示一次建議清單」控制接續行為。接續清單的**內容**由展開後的文字
決定，不是由片段本身指定：程式碼結尾落在 `FROM` 後面就只列資料表與檢視。

接續清單由本擴充自己重開，不是交給平台。提交時回報的 `CommitBehavior.Retrigger`
**在 SSMS 22 上是死的**：編輯器組件裡沒有任何一處讀那個旗標——Enter 與 Tab 只測
`RaiseFurtherReturnKeyAndTabKeyCommandHandlers`，輸入字元只測
`SuppressFurtherTypeCharCommandHandlers`。因此 `ssf` 展開成 `SELECT * FROM ` 之後
畫面就停在那裡，得再多打一個字母才等到清單。重開的做法見下一節。

### 儲存格式

存成一份 JSON，路徑是 `%APPDATA%\SqlAssist\snippets.json`，可以直接用編輯器改，
也可以整份複製到另一台機器。

```json
{
  "version": 1,
  "snippets": [
    {
      "shortcut": "ssf",
      "title": "SELECT * FROM",
      "description": "SELECT * FROM fragment",
      "triggerFollowUp": true,
      "code": "SELECT * FROM $table$$end$",
      "placeholders": [
        { "id": "table", "default": "", "tooltip": "資料表名稱" }
      ]
    }
  ]
}
```

讀取刻意寬容：允許 `//` 註解與尾隨逗號，認不得的欄位略過，壞掉的單一項目跳過。
整份檔案讀不成 JSON 時退回空清單而**不是**用預設清單覆蓋——使用者的內容還在
檔案裡，用預設清單蓋掉等於幫他刪光。管理介面會把錯誤原因顯示在下方。

這是 SqlAssist 自己的格式，**與 SSMS 的 `.snippet` XML 不互通**：
SSMS「程式碼片段管理員」（Ctrl+K, Ctrl+X）裡的內容不會出現在這裡。

不放進 Unified Settings 是因為那裡只收 boolean、integer、enum 與 string，
一份可增刪的清單塞不進去。
