# Query Memory：接續與分工

本頁記錄接手順序與工作邊界，不重述架構。每批完成後更新；以目前 Git 和測試校對，不盲信舊進度。

## 從哪裡開始

1. 讀 `CLAUDE.md`、[文件路由](index.md)及本批命中的護欄。
2. 檢查 branch、status、近期 commit；保留未提交變更，不因接手就 checkout／reset。
3. 讀[核心契約](query-memory.md)、[儲存決策](query-memory-storage.md)、[驗收與部署](query-memory-validation.md)。
4. 只定位本批相關型別與測試；先說明現況差異及本批範圍，再實作，不重新設計已完成部分。

原架構附件沒有納入 Git；跨電腦或其他 AI 不應假設能存取 Downloads。
現有決策以版本庫文件為依據；若本批涉及尚未記錄的產品規格，向使用者取得原附件，不自行補猜。

## 可核對的里程碑

| Commit | 已完成 |
|---|---|
| `74b2849` | 純核心內容位址、版本引擎、交易與分頁契約 |
| `d9551f1` | 有界背景寫入器、合併與交易衝突重試 |
| `6859668` | SQLite schema v1、WAL／去重／分頁、隔離層及封裝測試 |
| `dddabee` | SSMS 儲存自我測試命令 |
| `1b7fd2a` | 外部 LoadFrom 載入的代理轉型修正、回歸測試 |
| A 批（本次） | Debug 部署白名單、完整預檢、SHA-256 驗證與隔離 fixture |

最近產品驗證：完整測試 2826 項通過；Debug 部署隔離測試 59 項通過；
真正安裝目錄的 Debug 部署已成功驗證 11 個檔案並清除兩份快取；SSMS 儲存自我測試仍是使用者回報 PASS。
這不是整個功能完成：正式擷取、設定與 History UI 仍未啟用；其他實機門檻以驗收文件為準。

## A 批已完成

`Deploy-DebugExtension.ps1` 現在只替換白名單內的產品 DLL／PDB 與註冊 JSON；
Query Memory.Hosting／Sqlite 純程式碼改動已用真實安裝目錄成功 Deploy。provider、native、隔離設定、
pkgdef、Manifest 或其他安裝資產變更仍須 Install；完整條件見[Debug 部署完整性](debug-deployment.md)。

## 建議下一批

**B：儲存維護與 Saved Queries**。接續核心文件的後續批次，不重建 storage。
先定義保留／引用保護契約及 schema migration，再做有界清理、容量策略、Saved CRUD／scope。
活動 Recovery、Session head、Pinned、Manual 與 Saved 引用不得被清成懸空。
Saved 尚未落地時，不能宣稱已驗證 Saved 引用保護；要有真實 SQLite 回歸測試。

**C：擷取、設定與 UI**。待前置契約和實機門檻確認後，依核心文件分批完成。
不要因自我測試 PASS 就啟用實際 SQL 擷取；隱私設定與可見的降級提示仍未完成。

## 每批最低交付

依[開發流程](development.md)跑完整測試；產品／封裝改動另建 VSIX，儲存／載入改動另跑封裝 probe。
文件改動跑文件與文字檢查。說清楚要 Deploy 還是 Install、是否已在實機驗證；不把兩者混為一談。
