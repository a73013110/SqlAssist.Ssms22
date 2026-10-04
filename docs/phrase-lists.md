# 清單片語

本頁處理選項清單的片語怎麼探：逗號清單（`,*`）、項裡的等號、括號清單（`(*`）與剖析器落後的選項；
尾巴的寫法、展開與證據見[子句片語的產生器](phrase-generator.md)。

標頭開的逗號選項清單寫成 `ALTER USER {name} WITH ,*`，共用位置 `OptionItem`（各佔位元不夠）；
DDL 觸發程序的事件（`ON DATABASE FOR`）也是，事件依標頭而不同，寫完一項仍回報 `TriggerEventEnd`。
分析器走訪清單交出錨點，比對錨點前的標頭。
標頭的片語給第一項；逗號之後以每種第一項接逗號探測取聯集（用過的選項剖析器不收第二次），
探到新字再取第一個往下一項探（`VECTOR_SEARCH` 的引數順序固定）。一項接得了逗號就好，整句寫不寫得完不論；
值要過剖析器的（`FORMAT_TYPE =`）代入那一格列得出的第一個字，只收特定值的（`METRIC`）由 `Endings` 補。
項的等號之後立成中段清單片語（`… WITH ,* CHECK_POLICY =`）再往下一層：`ON`／`OFF`、`PASSWORD = 'x' HASHED`
在第幾項都一樣。沒有字、探測文字已有片語、這一項在這份清單不合法、標頭含 `...` 的不立。
官方有、ScriptDom 還不收的選項（`ALLOW_ENCRYPTED_VALUE_MODIFICATIONS`，TSql170 在值就報錯）寫在 `Lagging`：
唯一不經剖析器證明的字，只驗標頭；ScriptDom 跟上就刪。
`()` 探測代入 `(a)`，對括號內容有要求的（RAISERROR）由 `Group` 指定。

標頭夾著長度不定的一段（EXEC 的參數、BACKUP 的裝置清單）寫成 `EXEC ... WITH ,*`：尾巴的 `WITH`
對上了才找動詞，探測代入 `Gap`。仍各佔一個位置的：

- 括號清單：CREATE INDEX（含 XML、JSON 索引）與資料表定義裡內嵌索引的 `WITH (…)`，前面還夾著 `INCLUDE (…)` 與篩選 `WHERE`。標頭固定的括號清單
  （`ALTER TABLE t SET (`、`OPENROWSET (`）寫成 `(*` 片語；定義清單裡一項自己的括號清單也是（`CONNECTION (* {name}` 接 `TO`，
  前一格是 `ColumnDefinition`）。執行期分不出游標在 `(` 還是逗號之後，
  字取兩者聯集。括號裡是子句的
  （`WITHIN GROUP (ORDER BY …)`）寫 `Clause`，不取聯集。
- 選項寫完還要回報位置（模組的 `AS`、觸發程序的 `FOR`）；`EXECUTE AS` 這類多字選項以位置為鍵，掛到共用位置會漏進每一份清單。
- 不以逗號分隔：游標選項、序列選項（`SequenceOption`，`START WITH 1` 這種一項可以帶值）。
