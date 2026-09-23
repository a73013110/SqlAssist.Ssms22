using System;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Core.Notifications;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 把一個物件的結構送進新的查詢視窗：組指令碼與開窗寫入。
/// </summary>
/// <remarks>
/// F12（<see cref="SqlDefinitionOpener"/>）與 SQL Search 的「移至定義」共用這一份。
/// 兩條入口只差在那個物件是怎麼來的——一個是游標所在的識別字，一個是清單上選的那一列；
/// 從結構開始的三步完全相同。各留一份的症狀是其中一份忘了可執行性那三個選項，
/// 或忘了「緩衝區必須還是空的」那道守門，而後者會把指令碼蓋到使用者正在編輯的查詢上。
///
/// 指令碼內容本身仍然只有 <c>Metadata/Formatting/SqlObjectScript</c> 一份，這裡只接線。
/// </remarks>
internal static class SqlDefinitionScript
{
    /// <summary>把結構組成可以直接執行的定義指令碼。</summary>
    /// <remarks>
    /// 在<b>背景</b>執行緒呼叫。一份幾萬行的定義在 UI 執行緒上組等於畫面停一下，
    /// 而目的地是還沒開出來的空白視窗，沒有任何理由先等它。
    /// </remarks>
    public static SqlObjectScriptText Build(SqlObjectStructure structure, string documentName)
    {
        if (structure is null)
        {
            throw new ArgumentNullException(nameof(structure));
        }

        using (NotificationCenter.Default.Begin(
                   NotificationCatalog.GeneratingDefinitionScript,
                   NotificationKind.Navigation,
                   NotificationOrigin.User,
                   NotificationLevel.Info,
                   structure.Object.QualifiedName,
                   documentName))
        {
            // CreateForExecution 蓋掉的那三項是可執行性的要求，不是風格偏好；
            // 判斷只有那一份，這裡不重挑選項。
            return SqlObjectScript.BuildEditable(
                structure,
                SqlScriptPreferences.CreateForExecution(Environment.NewLine, structure.Object));
        }
    }

    /// <summary>
    /// 開一個沿用目前連線的空白查詢視窗，並把指令碼寫進去。
    /// </summary>
    /// <returns>成功時為 null，否則是要顯示給使用者的那一句。</returns>
    public static string? WriteToNewWindow(
        IServiceProvider serviceProvider,
        SqlObjectScriptText script,
        SqlObjectInfo objectInfo,
        string documentName,
        bool unconnected = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (objectInfo is null)
        {
            throw new ArgumentNullException(nameof(objectInfo));
        }

        return WriteToNewWindow(
            serviceProvider,
            script,
            objectInfo.QualifiedName,
            $"已在新查詢視窗開啟 {objectInfo.QualifiedName} 的定義",
            documentName,
            unconnected);
    }

    /// <summary>
    /// 開一個沿用目前連線的空白查詢視窗，並把指令碼寫進去。
    /// </summary>
    /// <param name="subject">
    /// 這一份指令碼講的是什麼；通知與復原描述用得到。<b>只是一句給人看的字</b>——
    /// 不是每一個送進查詢視窗的東西都有 <see cref="SqlObjectInfo"/>，SQL Search 的
    /// SQL Agent 作業就沒有。為那條路徑另寫一份開窗與寫入的症狀是其中一份忘了
    /// 「緩衝區必須還是空的」那道守門，而那一次會把指令碼蓋到使用者正在編輯的查詢上。
    /// </param>
    /// <param name="activityDescription">復原堆疊與診斷紀錄上的那一句。</param>
    /// <param name="unconnected">
    /// 開一個沒有連線的視窗，而不是沿用目前那一條（見
    /// <see cref="SsmsScriptWindow.TryCreateUnconnectedQuery"/>）。只有開窗那一步不同，
    /// 寫入與「緩衝區必須還是空的」那道守門是同一份：未連線的視窗一樣可能拿錯。
    /// </param>
    /// <returns>成功時為 null，否則是要顯示給使用者的那一句。</returns>
    public static string? WriteToNewWindow(
        IServiceProvider serviceProvider,
        SqlObjectScriptText script,
        string subject,
        string activityDescription,
        string documentName,
        bool unconnected = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (serviceProvider is null)
        {
            throw new ArgumentNullException(nameof(serviceProvider));
        }

        if (subject is null)
        {
            throw new ArgumentNullException(nameof(subject));
        }

        if (activityDescription is null)
        {
            throw new ArgumentNullException(nameof(activityDescription));
        }

        using var notification = NotificationCenter.Default.Begin(
            NotificationCatalog.OpeningQueryWindow,
            NotificationKind.Navigation,
            NotificationOrigin.User,
            NotificationLevel.Debug,
            subject,
            documentName);
        var view = unconnected
            ? SsmsScriptWindow.TryCreateUnconnectedQuery(serviceProvider, out var failure)
            : SsmsScriptWindow.TryCreateBlankQuery(serviceProvider, out failure);

        if (view is null)
        {
            notification.Fail();
            return failure;
        }

        var replacement = new TextReplacement(
            script.Text,
            SqlAssistActivityKind.DefinitionOpened,
            activityDescription,
            script.CaretOffset);

        // 空白查詢視窗的樣板是一個 0 位元組的檔案，所以這一道守門平常永遠成立。
        // 它擋的是「拿到的不是剛開的那個視窗」——那一次會把指令碼蓋到使用者
        // 正在編輯的查詢上，而那是無法復原的損失。
        if (new TextViewEditCoordinator(view).InsertIntoBlank(replacement))
        {
            return null;
        }

        notification.Fail();
        return "新查詢視窗不是空的，已取消寫入定義。";
    }
}
