# Query Memory：Saved Queries 與維護邊界

本頁是 B1 儲存契約；[核心](query-memory.md)的擷取及 UI 仍未啟用。

## B1 已實作

`ISavedQueryRepository` 是獨立能力，不改動擷取 writer／processor 的責任。
SQLite 與隔離 Hosting 均實作；沿用既有 `SavedQuery`、Revision、Content 與 ConnectionContext。

- CRUD 保存名稱、說明、scope、Pinned 與 `CurrentRevisionId`；只能引用已存在的不可變版本。
  不為收藏建立假 Session，不把修改名稱視為 SQL 執行或 Draft。
- 新增的 expectedVersion 為 null；更新／刪除使用讀取時的 GUID 版本 token。
  寫入成功換 token，刪除後重建也不重用。過期或重複新增回 Conflict，沒有部分寫入。
  Saved 不沿用 CaptureId 冪等契約；回應遺失後先重讀，不盲目覆寫。
- 更新引用及 Context 在同一 IMMEDIATE 交易完成。不存在的 Revision 由外鍵拒絕，包含
  新增 Context 的操作一併回復。刪除 Saved 不連帶刪除 Revision、Content、History。
- 所有 Saved 引用都受保護，不只有 Pinned；外鍵為 Saved → Revision → Content。
  既有 Recovery 替換只刪除沒有 Revision／Recovery／History 引用的 Content，因此沿用原 GC。
- 名稱為 1～200 字元且不可全空白；說明上限 2000 字元。SQL 全文不在 Saved metadata 內。

## Scope 與分頁

Global 不帶 Connection；Server 必須指定 Server 且 Database 留空；Database 兩者必填。
Server／Database 精確、區分大小寫，不以連線 Identity 做 scope 篩選。
讀取指定 scope 不隱含合併 Global／Server 父層，避免把清單可見度誤當 SQL 執行連線。

列表以不可變 SavedQueryId DESC 做 keyset paging，每頁 1～200 筆，多讀一筆判斷續頁。
游標綁定 StoreId 與 scope 篩選，不能與 History 或其他資料庫共用；頁數大小可調。
索引涵蓋 Scope、Server、DatabaseName、SavedQueryId。列表只投影共用的 240 字元預覽；
全文沿用 `ReadContentAsync` 延遲讀取。不提供全庫列表捷徑。
跨頁不是資料庫快照；新增或移動 scope 後須重新整理，不能保證看見分頁期間的所有修改。

## Schema v1 → v2

保留原 v1 建表流程，新庫與舊庫使用同一升級步驟。v2 僅新增 SavedQueries 與 scope／引用索引。
取得寫鎖後重讀 user_version，再於同一交易新增 schema 及更新版本，不重建資料庫或更換 StoreId。
取消或 DDL 失敗回復整個升級；未知、負數、未來版本與不相干資料庫仍拒絕開啟。

固定的 v1 SQL fixture 不引用產品最新 schema；真實 SQLite 回歸涵蓋有資料升級、並行開啟、
DDL 中途失敗回復及重試、根引用與 SQL 原文保留。自我測試驗證全新 v2 與 Saved CRUD，
不等同於真正 SSMS 既有資料庫的 migration 驗收。
舊版程式會拒絕 v2，沒有自動降版；回退程式不能靠修改 user_version 或刪庫解決。

## B2 必須沿用的保留契約

**尚未實作 retention／容量策略或全庫清理。** 下一批不能把本頁外鍵測試當成清理功能已驗收。

- 清理與新增／更新 Saved 都取得 IMMEDIATE 寫鎖；每批清理的引用檢查與刪除必須在同一交易，避免競賽。
- Saved、Pinned History、Manual Snapshot、活動 Recovery、Session 的兩種 head 都是保護根。
  Revision Parent／Execution 等外鍵也須處理；不能關閉外鍵或只靠年齡直接刪除。
- 保留期限與容量由政策輸入，不把建議值寫死在 Domain。清理筆數／工作量必須有界、可取消。
- 達容量仍有受保護資料時須回報無法回收，不破壞引用來達到上限。邏輯內容大小與 db／WAL
  實體檔案大小分開定義，不把刪除列當成磁碟已縮小。
- Context／Content 等孤立資料另做有界回收；不得在每次儲存收藏時掃描全庫。

Saved SQL 編輯／新增版本的交易流程、名稱／全文搜尋與 UI 可見範圍合併仍待後續批次。
本批只提供既有 Revision 的 CRUD／scope 基礎，不宣稱完整 Saved Queries 產品流程完成。
