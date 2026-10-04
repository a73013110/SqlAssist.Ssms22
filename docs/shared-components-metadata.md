# 中繼資料共用元件

本頁只列 Metadata 純邏輯（查詢、快取、模型與指令碼排版）的唯一出處，直接重用、不在功能目錄重寫；
Core 見[共用元件表](shared-components.md)，Ssms22 接線層見[平台共用元件](shared-components-platform.md)。

| 這件事 | 唯一出處 |
| --- | --- |
| 把連線指向同一台伺服器的另一個資料庫 | `Metadata/Querying/SqlDatabaseScopedConnectionSource.cs` |
| 把查詢指向連結伺服器（`OPENQUERY` 包裝、`sys.` 限定字、內嵌 object_id） | `Metadata/Querying/SqlCatalogQualifier.cs` |
| 限定字最左邊那段是結構描述、資料庫還是連結伺服器 | `Metadata/Model/SqlQualifierResolver.cs` |
| 目錄的快取鍵（伺服器＋資料庫＋連結伺服器） | `Metadata/Querying/SqlConnectionCacheKey.cs` |
| 指令碼宣告的資料來源換成物件明細（含宣告原文） | `Metadata/Model/SqlScriptTableDetail.cs` |
| 拿名稱向這份指令碼換宣告（Hover、預覽、F12 共用，名稱決定種類） | `Metadata/Model/SqlScriptDeclarations.cs` |
| 型別格式化 | `Metadata/Formatting/SqlTypeFormatter.cs` |
| 中繼資料快取與失敗降級 | `Metadata/Caching/SqlMetadataCatalog.cs` |
| 一個物件掛在誰身上（父物件、子物件的型別代碼、DEFAULT 的資料行） | `Metadata/Caching/SqlMetadataCatalog.GetParentAsync` |
| 物件總管節點的 URN（節點路徑、候選順序與跳脫） | `Metadata/Model/SqlObjectExplorerUrn.cs` |
| 搜尋索引的位元組預算、版本戳與失效 | `Metadata/Search/SqlCatalogSearchIndexCache.cs` |
| Hover、結構面板與 F12 的物件／欄位定位 | `Metadata/Model/SqlObjectLookup.cs` |
| 結果格線的值轉成 T-SQL 字面值 | `Metadata/ResultGrid/SqlValueLiteral.cs` |
| 重建資料表、型別、索引、條件約束與擴充屬性的排版 | `Metadata/Formatting/TSqlScriptRenderer.cs` |
| 單獨一個條件約束是哪一種、在父物件上的哪一列 | `Metadata/Model/SqlConstraintMatch.cs`、`SqlObjectStructure.FindConstraint` |
| 擴充屬性的 `sp_addextendedproperty` 八個引數 | `Metadata/Formatting/SqlExtendedPropertyScript.cs` |
| 說明收成單行與截斷 | `Metadata/Formatting/SqlDescriptionText.cs` |
| 檔頭、健檢與降級摘要的逐行 SQL 註解 | `Metadata/Formatting/SqlScriptComment.cs` |
| 索引選項的預設值是什麼 | `Metadata/Model/SqlIndexOptions.cs` |
| 結構健檢的規則與失敗隔離 | `Metadata/Analysis/SqlSchemaAnalyzer.cs` |
| 送進查詢視窗前的換行統一與游標落點 | `Metadata/Formatting/SqlObjectScript.cs` |
| 同義字與序列的 `CREATE` 定義 | `Metadata/Formatting/SqlCatalogScript.cs` |
