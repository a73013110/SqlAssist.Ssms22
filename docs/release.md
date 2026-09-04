# 版本、發布與安裝

## 版本號

版號的唯一來源是根目錄的 `version.json` 加上 git 歷史，由
[Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning) 在建置時計算。
專案檔、VSIX Manifest 與 README 都不再寫死版號。

```json
{ "version": "0.15" }
```

`version` 只寫 `major.minor`，第三段（patch）填的是 **git height**——從 HEAD 回推到
`version.json` 的 `version` 最後一次變動之間的 commit 數。因此：

| 產物 | 格式 | 範例（height 7） |
|---|---|---|
| VSIX Manifest／`AssemblyFileVersion` | `major.minor.height.commitId` | `major.minor.7.64243` |
| `AssemblyInformationalVersion` | `major.minor.height+commitId` | `major.minor.7+faf306205d` |
| `AssemblyVersion` | `major.minor.0.0` | `major.minor.0.0` |

第三段每個 commit 遞增，所以**每一次 commit 建出來的 VSIX 都能直接覆蓋安裝**，
不必再手動把 Manifest 的版號 +1。這正是舊版 Manifest 一路累加到 `0.13.14`，
而專案檔還停在 `0.13.1` 的原因。第四段由 commit id 推導、不遞增，只用來回推來源。

### 什麼時候要改 version.json

只有 **minor 或 major 要進位時**才改，patch 自己會走：

```powershell
# 把 version.json 的 minor 加一，開始新的一輪。改完 commit，height 歸零重算。
git commit -am "build: 版號進入 <新的 major.minor>"
git tag v<新的 major.minor>.0    # 選用，只是給人看的發布記錄，不影響版號計算
```

Tag 不參與版號計算，加不加都不影響建置結果。

### 四個會踩到的地方

- **改了程式卻沒 commit，版號不動。** height 是從 commit 算的，工作目錄的變更不列入。
  日常偵錯走 `Deploy-DebugExtension.ps1`（直接覆蓋檔案、不比對版號遞增），不受影響。
- **淺層 clone 會靜靜退成 `0.0.x`。** CI 上 `actions/checkout` 必須設
  `fetch-depth: 0`。`Test-VsixPackage.ps1` 會擋下這種版號，不會讓它包成 VSIX。
- **只改文件不會推進版號。** `version.json` 的 `pathFilters` 排除了 `docs/`、
  根目錄的說明文字（`README.md`、`CLAUDE.md`、`AGENTS.md`、`LICENSE`）、代理設定
  （`.claude/`、`.codex/`、`.mcp.json`）與只當參考的 `menus.decompiled.*`，
  因為那些內容不進 VSIX，不該讓已安裝的使用者看到一個「新版本」。
- **加 pathFilters 會讓版號倒退。** 排除項目變多，height 就重新算成一個更小的數，
  已安裝的使用者會覆蓋不了。所以調整 `pathFilters` 只能跟 minor 進位放在同一個
  commit——那時 height 本來就歸零，不存在倒退。

`Deploy-DebugExtension.ps1` 只比對已安裝與建置版號的 `major.minor`。兩者不同時
代表 pkgdef、vsct 或 Manifest 的註冊內容已經改變，必須重跑 `Install-Extension.ps1`，
光覆蓋 DLL 不夠。

## 發布

```powershell
.\tools\Publish-Release.ps1
```

腳本會依序確認發布前提（在 `master`、工作樹乾淨、本機沒有領先遠端、`gh` 已登入）、
跑測試、建 VSIX，然後以產物的 Identity 版號打 tag，在 GitHub 上建立**草稿** Release
並附上 VSIX。

**草稿是刻意的，不要改成直接發布。** VSIX 有一種只在實機才看得出來的失敗：MEF
匯出型別的命名空間變動後，SSMS 的元件快取會安靜地讓那些部件建立失敗——沒有例外、
沒有記錄，只有「功能整組消失」。`Test-VsixPackage.ps1` 驗得了封裝內容，驗不了這件事。
所以最後一關必須是人：把草稿的 VSIX 裝進 SSMS 確認過，再到 GitHub 按 Publish。

同一個 commit 只發布一次。tag 已存在時腳本會擋下來——有新變更就先 commit，
版號的 height 會自己往前。

這也是這個專案不架 CI 建置 VSIX 的原因：CI 複製得了建置，複製不了上面那一關。

## 安裝

先關閉所有 SSMS 視窗，再執行：

```powershell
.\tools\Install-Extension.ps1
```

安裝程式會開啟 SSMS 隨附的 `VSIXInstaller.exe`，請在畫面中確認安裝目標為
**SQL Server Management Studio 22**。安裝後重新啟動 SSMS。

## 解除安裝

先儲存查詢並關閉所有 SSMS 視窗，再執行：

```powershell
.\tools\Uninstall-Extension.ps1
```

預設會顯示 VSIXInstaller 確認介面，如需無介面模式加上 `-Quiet`。
解除安裝只移除 VSIX，會保留 `%LOCALAPPDATA%\SqlAssist.Ssms22` 內的設定與診斷紀錄。
