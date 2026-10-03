# 目錄物件名稱

本頁處理登入、使用者、角色、結構描述、憑證、金鑰這類只有目錄檢視列得出名稱的位置（`ALTER LOGIN `、
`DEFAULT_SCHEMA = `、`GRANT … TO `）；第一層的物件清單見[中繼資料](metadata.md)，語言名單見[名單](completion-instance-lists.md)。

## 一條規則

名稱那一格前面寫的是「哪一種」，就列那一種的既有名稱。種類一律取產生器的建立種類（`CreatedKinds`），
取最長的那一個：`DROP DATABASE SCOPED CREDENTIAL ` 是資料庫範圍認證，不是伺服器的認證；最長的那一種
不在名冊裡（`DROP EXTERNAL TABLE `）就不是這一格，照原本的目標走。判斷在 Core 的
`SqlCatalogEntityPosition`，四種寫法問同一份名冊（`SqlCatalogEntity`）：

| 寫法 | 例 | 證據 |
|---|---|---|
| 類別之後的 `::` | `GRANT … ON SCHEMA::`、`ALTER AUTHORIZATION ON LOGIN::` | 詞元 |
| 名稱格片語，尾巴是種類 | `ALTER LOGIN `、`DROP USER IF EXISTS `、`CREATE USER u FOR LOGIN `、`OPEN SYMMETRIC KEY ` | 片語確定、`TakesName`、不封閉 |
| 名稱格片語，尾巴是 `AUTHORIZATION`、`MEMBER` | `CREATE SCHEMA s AUTHORIZATION `、`ALTER ROLE r ADD MEMBER ` | 主體，範圍取那一句的種類 |
| 清單片語一項的 `X =` | `DEFAULT_DATABASE = `、`DEFAULT_SCHEMA = `、`LOGIN = `、`CREDENTIAL = ` | X 去掉 `DEFAULT_` 是種類 |
| 主體的位置 | `GRANT … TO `、`REVOKE … FROM a, `、`ALTER AUTHORIZATION … TO `、稽核動作的 `BY ` | `PermissionGrantee` |

`CREATE 種類 ` 之後是新名字，不算。`X =` 只認清單片語的一項（位置分析找得到錨點）：`UPDATE t SET DEFAULT_SCHEMA = `
是資料行；括號清單（端點的 `ROLE =`）的值不是這份名冊的名稱。`DEFAULT_LANGUAGE =` 不在名冊裡，由語言名單給。

判定成立時目標是 `CatalogEntity`，種類放在 `SqlCompletionContext.CatalogEntities`，排在封閉片語之前：
擁有者那一格（`CREATE CERTIFICATE c AUTHORIZATION `）的片語寫不完整句。

## 主體的範圍

| 位置 | 列哪一層 |
|---|---|
| `ON` 伺服器那一層的類別（`LOGIN::`、`ENDPOINT::`、`SERVER ROLE::`） | 登入與伺服器角色 |
| `ON` 其餘類別或物件（`SCHEMA::`、`dbo.Loan`）、`DATABASE::` 的權限 | 使用者與資料庫角色 |
| `ALTER AUTHORIZATION ON DATABASE:: … TO` | 登入與伺服器角色：擁有者取類別住的那一層 |
| 沒有 `ON`（`GRANT VIEW SERVER STATE TO`、`GRANT CREATE TABLE TO`） | 兩層都列，說不出是哪一層 |
| `AUTHORIZATION`、`MEMBER` | 那一句的種類住的那一層；名冊沒有的種類（外部程式庫）住在資料庫 |

資料庫是唯一兩邊不同的：住在伺服器上、擁有者是登入，權限卻授給它自己的使用者（`SqlCatalogEntity.GranteeScope`）。

## 清單裡的其餘東西

片語的字照接上來：`ALTER DATABASE ` 的 `CURRENT`、`DROP USER ` 的 `IF EXISTS`、`DEFAULT_SCHEMA = ` 的 `NULL`。
目錄的關鍵字照位置過濾：主體的位置由產生器探出 `PUBLIC` 與擁有者的 `SCHEMA`（`OWNER` 由片語接）；
`::` 與選項的值那一格不收任何關鍵字，位置記成 `None`——判不出位置的整份目錄會把名稱淹掉。
資料表、程序這些第一層物件不列，`FROM` 之後也不列登入。插入文字照[插入文字](completion-insertion.md)的規則加括號
（`[LIBRARY\Reader]`）。

## 名單與降級

Metadata 的 `SqlCatalogEntityQuery` 是「種類 → 目錄查詢」的名冊，測試核對每一種都有。結構描述與資料庫取第一層快照
（結構描述取完整名單：`DROP SCHEMA` 要的多半是空的那一個），其餘查 `sys.server_principals`、`sys.database_principals`、
`sys.certificates` 這些檢視。系統自己的主體與金鑰（`##MS_…##`、`sys`、`INFORMATION_SCHEMA`）不列；2016 才有的檢視先問
`sys.all_views`，舊版是一份成功的空名單。

每份目錄各存一份、有效期與第一層相同、跟著 `Invalidate` 清掉：`CREATE LOGIN` 剛執行完，下一句就要列得出它。
一律問目前這條連線；連結伺服器的目錄回空。看不到的列由伺服器依權限濾掉，不是錯誤；`DbException` 照
[相容與失敗](metadata-compatibility.md)降級成空名單、不進快取，退避期間不再試。通知標題相同，主體是種類名稱。
關掉「列出資料庫物件與欄位」時不查，只剩片語與位置的字。
