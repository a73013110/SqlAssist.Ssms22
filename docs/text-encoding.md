# 文字檔格式與輸出編碼

## PowerShell 輸出編碼

工具一律使用 **PowerShell 7+**。檔案是 UTF-8，不代表子程序的輸出也會是 UTF-8；
Git GUI、終端機與無主控台程序可能使用不同代碼頁。所有 PS1 在執行工作前共用：

```powershell
Import-Module (Join-Path $PSScriptRoot 'SqlAssist.Tools.psm1') -Force
# 回傳給目前腳本的作用域，避免只改到模組內的管道偏好。
$OutputEncoding = Initialize-SqlAssistUtf8Output
```

父程序若用 ProcessStartInfo 讀取這些腳本，stdout／stderr 解碼也要明確指定 UTF-8；
只指定父端編碼不會替子程序轉碼。[Microsoft 說明](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.standardoutputencoding)
建置查詢 vswhere 另加 `-utf8`，避免中文安裝路徑被破壞。

`Check-TextFiles.ps1` 以語法樹確認 `tools/` 每支腳本都先執行上面兩行。不以略過 hook 解決亂碼。

## 文字檔格式

所有文字檔統一為 **UTF-8 與 LF**；除 `.sln` 保留 BOM 外，其餘檔案不含 BOM。根目錄的
`.gitattributes` 會覆蓋 Windows 全域的 `core.autocrlf=true`，`.editorconfig` 則讓支援它的編輯器在儲存時沿用
同一份規則。這樣產生器或補丁工具寫出的 LF 不必再整檔「還原 CRLF」。

push 前的 hook 會先執行下列檢查。遇到 CR 或 CRLF 會直接轉成 LF；遇到
BOM（`.sln` 除外）、無效 UTF-8 或缺少檔尾換行仍會停止：

```powershell
.\tools\Check-TextFiles.ps1
```
