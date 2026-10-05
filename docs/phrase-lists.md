# 清單片語

本頁處理選項清單的片語怎麼探：逗號清單（`,*`）、項裡的等號、括號清單（`(*`）與剖析器落後的選項；
尾巴的寫法、展開與證據見[子句片語的產生器](phrase-generator.md)。

標頭開的逗號選項清單寫成 `ALTER USER {name} WITH ,*`，共用位置 `OptionItem`（各佔位元不夠）；
DDL 觸發程序的事件（`ON DATABASE FOR`）也是，事件依標頭而不同，寫完一項仍回報 `TriggerEventEnd`。
分析器走訪清單交出錨點，比對錨點前的標頭。
GRANT、DENY、REVOKE 的權限也是（`GRANT ,*`），一項的開頭由權限清單的走訪回報 `OptionItem`，寫完一個權限照舊是 `PermissionList`。
中段 `,*` 之後是字面字時，那個字開始一項、前面緊接標頭或逗號，否則 `GRANT SELECT ON EXTERNAL` 的類別會比對成權限。
標頭的片語給第一項；逗號之後以每種第一項接逗號探測取聯集（用過的選項剖析器不收第二次），
探到新字再取第一個往下一項探（`VECTOR_SEARCH` 的引數順序固定）。一項接得了逗號就好，整句寫不寫得完不論；
值要過剖析器的（`FORMAT_TYPE =`）代入那一格列得出的第一個字，只收特定值的（`METRIC`）由 `Endings` 補。
項的等號之後立成中段清單片語（`… WITH ,* CHECK_POLICY =`）再往下一層：`ON`／`OFF`、`PASSWORD = 'x' HASHED`
在第幾項都一樣。沒有字、探測文字已有片語、這一項在這份清單不合法、標頭含 `...` 的不立。
官方有、ScriptDom 還不收的選項（`ALLOW_ENCRYPTED_VALUE_MODIFICATIONS`，TSql170 在值就報錯）寫在 `Lagging`：
唯一不經剖析器證明的字，只驗標頭；ScriptDom 跟上就刪。
剖析器什麼都收的清單探不出字：`GRANT` 收任何一串識別字（連 `AND` 都收），權限名稱由 `Evidence` 手寫（`sys.fn_builtin_permissions`），
每一條接在尾巴之後當證據、一個字一個字列。探到的字不算數：標頭與逗號之後只列證據的第一個字並封閉，證據中段立起的片語
（`GRANT ,* VIEW`）是帶尾巴的附加片語，`DEFINITION` 加在 `PermissionList` 的 `ON`、`TO` 旁邊——`CONTROL` 寫完了也還接 `SERVER`。
`()` 探測代入 `(a)`，對括號內容有要求的（RAISERROR）由 `Group` 指定。

標頭夾著長度不定的一段（EXEC 的參數、BACKUP 的裝置清單、統計資料的資料行清單與篩選）寫成 `EXEC ... WITH ,*`：尾巴的 `WITH`
對上了才找動詞，探測代入 `Gap`。宣告的 `Endings` 標頭也用：`RESTORE … WITH MOVE` 要寫完 `'a' TO 'b'` 才是一項。

備份裝置本身也是 `,*` 清單（`BACKUP DATABASE {name} TO ,*`），寫完一個裝置寫成中段的 `,* {value}`（接 `MIRROR TO`、`WITH`）：
`DISK` 是關鍵字、不是運算元，`TO DISK ` 之後不會比對成寫完；寫 `{name}` 的話剖析器把它當邏輯裝置名稱。

清單裡一項自己的寫法（`ENCRYPTION (`、`SAMPLE n`、`ACTIVATION (`）寫成 `After = ["OptionItem"]`，`Template` 指那份清單的樣板。
樣板的名稱寫得與清單片語的探測文字相同（`CREATE QUEUE t WITH `），否則認不出標頭已列那個字，另立成附加片語。
`OptionItem` 不放寫一個選項就完整的敘述（`UPDATE STATISTICS t WITH ALL`）：否則 `ALL`、`INDEX` 被判成寫完一項的字，
`REBUILD PARTITION = ALL` 的 `ALL` 就成了值。那種敘述的項以 `Lead` 認（`RESAMPLE ON`）。

仍各佔一個位置的：

- 括號清單：CREATE INDEX（含 XML、JSON 索引）與資料表定義裡內嵌索引的 `WITH (…)`，前面還夾著 `INCLUDE (…)` 與篩選 `WHERE`。標頭固定的括號清單
  （`ALTER TABLE t SET (`、`OPENROWSET (`）寫成 `(*` 片語；定義清單裡一項自己的括號清單也是（`CONNECTION (* {name}` 接 `TO`，
  前一格是 `ColumnDefinition`）。執行期分不出游標在 `(` 還是逗號之後，
  字取兩者聯集。括號裡是子句的（`WITHIN GROUP (ORDER BY …)`、`WAITFOR (RECEIVE …)`）寫單獨的 `(`：只認緊接的左括號、不取聯集，
  否則子句裡的逗號也比對成清單項，`RECEIVE a, ` 之後只剩子句開頭的字。
- 選項寫完還要回報位置（模組的 `AS`、觸發程序的 `FOR`）；`EXECUTE AS` 這類多字選項以位置為鍵，掛到共用位置會漏進每一份清單。
- 不以逗號分隔：游標選項、序列選項（`SequenceOption`，`START WITH 1` 這種一項可以帶值）。
