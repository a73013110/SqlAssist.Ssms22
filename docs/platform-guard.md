# 平台 Guard 的三族 API

`SqlAssistPlatformGuard` 只收斂 SSMS／Visual Studio 平台邊界；使用前先讀
[平台接線護欄](rules-platform.md)。

| 方法 | 用在哪 | 失敗時 |
|---|---|---|
| `Run`／`RunAsync`／`Create` | MEF 建立、按鍵、編輯器事件、派送工作 | `WriteAlways` 完整堆疊並回傳替代值 |
| `Probe` | 佈景筆刷、DPI、游標位置、錨點座標等可選探測 | 只在詳細診斷記一行 |
| `Begin`／`BeginProbe` | 沒有人接結果的背景工作；後者用於預載與預熱 | 依前兩族的層級處理 |

`Probe` 用於會連續失敗的探測，避免紀錄淹沒真正錯誤。取消通常視為正常結束；只有
`RunPropagatingCancellation` 必須把取消狀態交回平台，否則過期內容會被當成有效答案。
它的替代值使用 `Func<T>`，避免每次成功也先計算昂貴的完整候選清單。

以下四類刻意不走 Guard，且程式碼必須寫明理由：

- 使用者主動觸發、失敗必須看見：工具命令、預覽狀態列、片段管理員與 F12。
- `SqlAssistPackage` 載入失敗：記錄後重擲，讓殼層知道套件未載入。
- 有例外篩選的預期失敗：例如片段存放區只接檔案系統錯誤。
- `SqlAssistDiagnostics` 本身：Guard 的錯誤正要寫到這裡。
