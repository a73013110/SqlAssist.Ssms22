# 中繼資料護欄

修改查詢、快取、結構模型或可執行指令碼前必讀。
- **禁止**讓 `DbException` 冒出 `SqlMetadataCatalog`。連不上、逾時、權限不足在
  `TryLoad` 降級成「這一輪沒有資料」；冒出去會讓平台邊界每按一次鍵記一份完整堆疊。
  只接 `DbException`，失敗不進快取，理由見[相容與失敗](metadata-compatibility.md)。

- **禁止**在資料不齊時輸出半份可以執行的東西。種類問
  `SqlObjectKinds.HasExecutableScript`、這一次查到的資料問
  `SqlObjectStructure.CanBuildExecutableScript`，任何一道不過就整段換成註解，
  寫明缺什麼、兩個可能的原因與查得到的部分（格式只有 `BuildUnavailableScript` 一份）。
  查詢成功卻一列都沒有回來是常態不是例外：物件清單是快取的，中繼資料的可見度
  照權限過濾。少了欄位的 `CREATE TABLE` 只剩一對空括號，卻仍然貼得上去，
  理由見[相容與失敗](metadata-compatibility.md)。

- **禁止**在查不到跨資料庫或跨伺服器的名稱時，退回拿目前連線裡同名的物件回答。
  建議清單、F12、滑鼠停留、`SELECT *` 展開四條路都曾經這樣退，而畫面上完全
  看不出退過——那比什麼都不做糟，什麼都不做至少是沉默。名稱的段數由
  `Core/Parsing/SqlObjectPath` 保留，物件屬於哪個資料庫由 `SqlObjectInfo.DatabaseName`
  帶著走；`object_id` 只在自己那個資料庫裡唯一，任何以它查快取的地方都要先換目錄。

- **禁止**讓連結伺服器（四段式名稱）走本機或跨資料庫的一般載入。必須使用獨立目錄、
  `SqlCatalogQualifier` 與 `OPENQUERY`，並配短逾時與失敗退避；查不到就沒有建議，絕不
  回退本機同名物件。理由見[遠端中繼資料](metadata-remote.md)。

- **禁止**預先載入所有進得去的資料庫。第一層快照是常駐的，共用主機上等於幾十輪
  查詢與幾十份常駐快照，而其中九成九不會有人用到。只在使用者真的打出資料庫名稱
  之後才建目錄，而且跨資料庫的目錄數量要有上限。
