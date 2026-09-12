# Query Memory：SSMS 實機驗收

自我測試是[SQLite 儲存](query-memory-storage.md)的載入門檻，不代表 History 功能已啟用。
命令只在開啟 `sqlAssist.diagnostics.verboseLogging` 後出現，不依賴目前查詢或連線。

## 操作

1. 儲存工作並關閉所有 SSMS 視窗，使用 `tools/Install-Extension.ps1` 安裝本次建置。
   此批新增命令，命令資源已升至 21；不可只部署 DLL，詳見[安裝](release.md#安裝)。
2. 啟動 SSMS，在「工具 → SqlAssist → 設定」開啟詳細記錄。
3. 執行「工具 → SqlAssist → Query Memory 儲存自我測試…」。
   狀態列會顯示執行中，同一程序不接受重複執行；完成後原生訊息框顯示結果與報告路徑。
4. 第一次通過後，再跑一次；確認兩次皆通過，打字、補全、物件總管與既有 SQLite 相依功能正常。
   可對測試用 SQL Server 執行 `SELECT 1;`，確認執行與結果格線未受影響；自我測試本身不會執行 SQL。
5. 關閉並重啟 SSMS，再測一次。也可先操作平常使用的宿主功能，再跑自我測試，檢查載入順序差異。
6. 若要驗證解除安裝，先儲存工作並關閉 SSMS，依[解除安裝](release.md#解除安裝)操作。
   重新開啟確認既有功能正常，再重新安裝；不需刪除任何使用者設定或資料。

請回報 SSMS／SqlAssist 版本、成功或失敗、報告與異常步驟；不需要提供業務 SQL 或連線字串。
報告含本機目錄與程序資訊，分享前可遮蔽使用者名稱等私人路徑。

## 自動驗證範圍

`QueryMemory.Hosting/QueryMemoryStorageSelfTest` 是 SSMS 命令及封裝 probe 的唯一實作：

- 背景建立專用資料庫，保存 21 次內建圖書館範例 SQL，驗證冪等重送不重複新增。
- 卸載隔離 AppDomain，再重新開啟，驗證筆數、內容位址共用與全文還原。
- 再次卸載，獨占開啟資料庫，確認檔案控制代碼已釋放。
- 比對宿主 AppDomain 前後的 provider 組件；新增 provider 即回報失敗。
- 報告逐步落盤，失敗保留最後成功步驟與例外；取消或失敗不宣稱通過。

每次測試資料存於 `%LOCALAPPDATA%\SqlAssist.Ssms22\QueryMemorySelfTest\<唯一識別碼>`。
`report.txt` 與 `self-test.db` 都是測試產物，不含使用者 SQL，測試不會覆寫既有報告或資料庫。
為保留失敗證據，不自動清除；驗收完可在 SSMS 關閉後自行刪除本次唯一識別碼目錄。

**PASS 不代表** native DLL 已從程序卸載、所有宿主功能均相容，或安裝／解除安裝已驗證。
這些仍須上述人工操作確認；在此之前維持 SQL 擷取停用。
