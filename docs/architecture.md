# 架構與平台邊界

## 三個專案

| 專案 | 目標 | 相依 | 職責 |
|---|---|---|---|
| `SqlAssist.Core` | netstandard2.0 | 無 | 詞法、剖析、排名、設定模型；純文字進、純結果出 |
| `SqlAssist.Metadata` | netstandard2.0 | `System.Data` | 中繼資料查詢、模型與快取 |
| `SqlAssist.Ssms22` | net48 | SSMS 組件 | MEF、命令、視窗、設定與編輯器接線 |

分層只問：**這段邏輯是否需要 SSMS 才跑得起來？** 不需要就放 Core 或 Metadata，
需要才放 Ssms22，而且薄到只剩取得服務、掛事件與套用結果。

`SqlCompletionTriggers` 是樣板：Core 判斷文字與游標，Ssms22 只在正確時機呼叫並轉成
`OpenOrUpdate`。`SqlInsertionText` 也曾因共用型別放錯到 Ssms22；把不碰資料庫的識別字
括號化移回 Core 後，整條插入規則才重新可測。**放置位置由相依決定，不由現址決定。**

## 導航與唯一實作

- 資料夾職責與測試鏡像見[資料夾對應](folder-map.md)。
- 已知症狀但不知道型別時才查[程式碼路徑表](code-map.md)。
- 新增跨功能邏輯前查[共用元件表](shared-components.md)，不要再造第二份。
- 平台例外收斂的強制規則見[平台接線護欄](rules-platform.md)，三族 API 的差異見
  [平台 Guard](platform-guard.md)。

## 為什麼使用平台原生補全管線

SSMS 的 T-SQL IntelliSense 是舊版語言服務，官方文件沒有保證新版 async completion API
會套用到 `ContentType "SQL"`，因此先實機量測再決定。SSMS 22.9.12105.275 確認
`GetOrCreate` 與 `InitializeCompletion` 都會收到按鍵；建立 TextView 當下
`IsCompletionSupported` 回報 `False` 只是建議來源尚未實例化的時序結果。

自製 WPF 清單無法根治三件事：與內建清單同時出現、只能靠鍵盤操作、反覆
`DismissAllSessions` 搶 session。0.13.0 起改走原生管線，分成三個 MEF 匯出：

| 匯出 | 職責 |
|---|---|
| `IAsyncCompletionSource` | 項目與說明面板 |
| `IAsyncCompletionItemManager` | 排名、篩選與命中標示 |
| `IAsyncCompletionCommitManager` | 提交、接續建議與語句展開 |

排名器不能省：平台預設比對器沒有詞首感知，否則 `libr` 排不到 `Lib_Reader`。
逐次診斷由「設定 → SqlAssist → 診斷 → 寫入詳細診斷紀錄」開啟。
