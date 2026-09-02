using System;
using System.Globalization;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Metadata.ResultGrid;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.ResultGrid;

/// <summary>
/// 結果格線右鍵選單上那些命令實際做的事。
/// </summary>
/// <remarks>
/// 每個命令的形狀都一樣：找格線 → 讀出一塊資料 → 產指令碼 → 交出去。
/// 前兩步共用 <see cref="Prepare"/>，最後一步各自不同，理由是兩種產出的用法不同：
///
/// <c>#temp</c> 是一段完整可執行的指令碼，開進<b>新的查詢視窗</b>——它沿用同一個
/// 連線，按 F5 就能跑。塞進剪貼簿的話使用者還要自己找地方貼，而那份東西有二十幾行。
///
/// <c>IN</c> 條件是一段<b>述詞</b>，進剪貼簿——它要貼進使用者手上那一句 SQL 的
/// <c>WHERE</c> 後面，開一個新視窗反而多一次搬運。
///
/// 這裡不走 <see cref="SqlAssistPlatformGuard"/>：CLAUDE.md 明文禁止用它處理
/// 「使用者按了卻沒反應」的失敗。每一種失敗都要說出自己的那一句，
/// 而 Guard 會把它們全部收斂成一次靜靜的什麼都不做。
/// </remarks>
internal static class ResultGridActions
{
    /// <summary>把選取範圍寫成 <c>#temp</c> 的建表與灌資料指令碼，開進新的查詢視窗。</summary>
    public static void CreateTempTableScript(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!Prepare(serviceProvider, out var table))
        {
            return;
        }

        var script = SqlTempTableScript.Build(table!);
        var view = SsmsScriptWindow.TryCreateBlankQuery(serviceProvider, out var failure);

        if (view is null)
        {
            SqlAssistStatusBar.Show(serviceProvider, failure);
            return;
        }

        var replacement = new TextReplacement(
            script,
            SqlAssistActivityKind.ResultGridScripted,
            Describe(table!, "已在新查詢視窗建立 #temp 指令碼"),
            caretOffset: 0);

        // 空白查詢視窗的樣板是零位元組的檔案，所以這一道守門平常永遠成立。
        // 它擋的是「拿到的不是剛開的那一個」——那一次會蓋掉使用者正在編輯的查詢。
        if (!new TextViewEditCoordinator(view).InsertIntoBlank(replacement))
        {
            SqlAssistStatusBar.Show(serviceProvider, "新查詢視窗不是空的，已取消寫入指令碼。");
        }
    }

    /// <summary>把選取範圍寫成可以接在 <c>WHERE</c> 後面的條件，複製到剪貼簿。</summary>
    public static void CopyInPredicate(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!Prepare(serviceProvider, out var table))
        {
            return;
        }

        var predicate = SqlInPredicateScript.Build(table!);

        try
        {
            Clipboard.SetText(predicate);
            SqlAssistStatusBar.Show(serviceProvider, Describe(table!, "已複製 IN 條件"));
        }
        catch (Exception exception)
        {
            // 剪貼簿被別的程序鎖住時會擲例外。這一句一定要說出來——
            // 使用者接下來要按的是 Ctrl+V，而那時候貼出來的是舊的東西。
            SqlAssistDiagnostics.WriteAlways($"複製 IN 條件失敗：{exception}");
            SqlAssistStatusBar.Show(serviceProvider, $"複製 IN 條件失敗：{exception.Message}");
        }
    }

    /// <summary>命令什麼時候可用：SqlAssist 啟用，而且找得到一個有資料的結果格線。</summary>
    /// <remarks>
    /// 這是右鍵選單每次彈出都會問到的路徑，所以只做「找不找得到格線」這一件事，
    /// 不去讀選取範圍、更不讀資料。停用的命令在選單上仍然看得見，
    /// 使用者因此知道這個功能存在，只是現在沒有東西可以做。
    /// </remarks>
    public static bool IsAvailable() =>
        SqlAssistSettingsStore.Current.Enabled
        && SsmsResultGrid.TryGetActive(out _, out _);

    private static bool Prepare(IServiceProvider serviceProvider, out ResultGridTable? table)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        table = null;

        if (!SsmsResultGrid.TryGetActive(out var grid, out var failure))
        {
            SqlAssistStatusBar.Show(serviceProvider, failure);
            return false;
        }

        if (!grid!.TryRead(out table, out failure))
        {
            SqlAssistStatusBar.Show(serviceProvider, failure);
            return false;
        }

        return true;
    }

    /// <remarks>
    /// 訊息一定要帶形狀。實測的結果有 178 欄，「已複製」三個字答不出使用者真正
    /// 想確認的那件事：我剛剛到底選到了什麼。
    /// </remarks>
    private static string Describe(ResultGridTable table, string action) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}：{1} 欄 × {2} 列{3}。",
            action,
            table.Columns.Count,
            table.Rows.Count,
            table.IsWholeResult ? "（整份結果）" : "（選取範圍）");
}
