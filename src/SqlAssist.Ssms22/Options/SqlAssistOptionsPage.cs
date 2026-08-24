using System;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Core;

namespace SqlAssist.Ssms22.Options;

/// <summary>
/// SqlAssist 設定頁的共用基底。
/// </summary>
/// <remarks>
/// 這些頁面刻意<b>不</b>使用 VS 的設定存放區，而是直接讀寫 settings.json：
/// 工具選單的即時開關、手動編輯設定檔與這裡的對話框必須看到同一份狀態，
/// 兩套儲存體會造成兩邊互相覆蓋。
/// </remarks>
public abstract class SqlAssistOptionsPage : DialogPage
{
    private protected static SettingsService Settings => SettingsService.Default;

    /// <summary>從 settings.json 填入頁面屬性。</summary>
    private protected abstract void LoadFrom(SqlAssistSettings settings);

    /// <summary>把頁面屬性寫回設定物件。</summary>
    private protected abstract void ApplyTo(SqlAssistSettings settings);

    public override void LoadSettingsFromStorage()
    {
        try
        {
            LoadFrom(Settings.GetSnapshot());
        }
        catch (Exception exception)
        {
            // 設定頁不可以因為檔案問題而讓「工具→選項」整個開不起來。
            SqlAssistDiagnostics.WriteAlways($"載入設定頁失敗：{exception.Message}");
        }
    }

    public override void SaveSettingsToStorage()
    {
        try
        {
            Settings.Update(ApplyTo);
            SuggestionRefreshBroker.RequestRefresh(); // 不必重新啟動 SSMS 就生效。
            SqlAssistDiagnostics.WriteAlways($"設定頁已套用：{GetType().Name}");
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"儲存設定頁失敗：{exception.Message}");
        }
    }

    /// <summary>對話框開啟時重新讀檔，確保顯示的是磁碟上的最新內容。</summary>
    protected override void OnActivate(System.ComponentModel.CancelEventArgs eventArgs)
    {
        LoadSettingsFromStorage();
        base.OnActivate(eventArgs);
    }
}
