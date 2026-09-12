# Query Memory：Saved Queries 與維護邊界

本頁是 B1 儲存契約；B2a 見[有界維護](query-memory-maintenance.md)，擷取及 UI 仍未啟用。

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
舊版程式會拒絕較新 schema，沒有自動降版；回退程式不能靠修改 user_version 或刪庫解決。

## 搜尋

`SavedQueryRequest.Search` 與 History 同語意：區分大小寫的字面子字串，不是 FTS 也不吃萬用字元。
null 與空字串代表不篩選；空白本身是合法的搜尋內容。

- 名稱、說明或目前版本的 SQL 全文任一命中即納入，三者取聯集，不回報命中的是哪一個欄位。
- 搜尋只在指定 scope 之內，不合併 Global／Server 父層，也不改變 SavedQueryId DESC 的 keyset。
- 名稱與說明由 SQLite 以 UTF-8 位元組比對，SQL 全文沿用 History 的 UTF-16 KMP 掃描；
  TEXT 條件排在 BLOB 之前，靠 OR 短路讓多數候選不必解出全文。說明為 NULL 不影響其他條件。
- 游標指紋含搜尋字串，換字或清空即失效；跨頁一樣不是資料庫快照，改名後要重新整理。

## 自動驗證

B1 的 CRUD／scope／引用保護紀錄見[實機驗收](query-memory-validation.md)。
2026-09-12，提交 `f6a82b3` 後完整測試 2890 項成功、0 失敗、0 略過（B3 搜尋新增 4 項）：

- 名稱、說明與 SQL 全文任一命中；說明為 NULL 的收藏仍靠全文命中，不因 `instr` 回傳 NULL 消失。
- 大小寫不同、尾隨空白與注入字串都不命中；搜尋不把其他 scope 的相同 SQL 帶進結果。
- 篩選後仍是 SavedQueryId DESC keyset；換字或清空搜尋即拒絕沿用舊游標。
- `EXPLAIN QUERY PLAN` 確認加上搜尋條件仍走 `IX_SavedQueries_ScopeId`、沒有 TEMP B-TREE。
- 取消不讓 `SqliteException` 外流；History 搜尋維持只比對 SQL 全文，不含文件顯示名稱。
- 封裝 probe 以真實 VSIX 與宿主設定重跑，自我測試報告含名稱與全文搜尋、大小寫敏感三項。

## 維護與後續邊界

清理的保護根、容量定義、交易競賽與續跑契約統一見[有界維護](query-memory-maintenance.md)。
B1 的外鍵測試不等於完整容量清理驗收，新增測試與限制見[維護驗收](query-memory-maintenance-validation.md)。

Saved SQL 編輯／新增版本的交易流程與 UI 可見範圍合併仍待後續批次。
本批只提供既有 Revision 的 CRUD／scope／搜尋基礎，不宣稱完整 Saved Queries 產品流程完成。
