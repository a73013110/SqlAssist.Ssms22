# 設定護欄

修改 Settings、註冊 JSON 或設定頁前必讀。
新增一個設定必須同時動四處，漏掉後三處的任何一處都是建置失敗（不是執行期回退）：
`SqlAssist.registration.json`、`Core/Settings/SqlAssistSettings` 屬性、
`SqlAssistMonikers` 常數、`SqlAssistSettingsReader.Read()` 對應。

- **禁止**手寫 moniker 清單。`SqlAssistMonikers.All` 由反射產生。
- **禁止**讓註冊檔的 `default` 與 POCO 的屬性預設值分歧。
- **禁止**讓 `enableWhen`／`visibleWhen` 跨分類參照——殼層會安靜地讓整個設定頁消失。
- **禁止**讓設定的 `enableWhen` 參照一個以上的設定；同分類也不行，那一項會安靜地消失。
  設定頁的縮排就是照這個參照排的，所以參照誰＝排在誰底下。
- **禁止**改動既有 `enum` 的字面值；那等於讓所有使用者的設定回退到預設。
- **禁止**把清單型資料放進 Unified Settings，它只收 bool／int／enum／string。
- **禁止**在取不到 Unified Settings 服務時讓擴充停擺；一律回退到內建預設值。
- **禁止**只為「可設定」就新增設定；不用時沒有成本、沒有實際分歧的行為不需要旋鈕。
