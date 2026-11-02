using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Threading;
using EnvDTE;
using Microsoft.VisualStudio.PlatformUI.Shell;
using Microsoft.VisualStudio.PlatformUI.Shell.Preferences;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Core.Connections;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Connections;

/// <summary>
/// 替殼層記住「文件索引標籤著色方式」選了資料庫，重開 SSMS 後套回去。
/// </summary>
/// <remarks>
/// 殼層的設定只認內建的三種著色方式，第三方提供者一律存成無著色，也沒有開放註冊；
/// 不記的話每次重開都要再選一次。判斷規則見 <see cref="SqlDatabaseTabChoice"/>。
/// </remarks>
internal static class SqlDatabaseTabChoiceMemory
{
    /// <summary>殼層 FileColor 命令表的「無著色」；要分辨使用者關閉與殼層重播只能看這個命令。</summary>
    private const string SwitcherCommandSet = "{FBD5D891-86F7-40F0-B635-0B95D4E62041}";
    private const int NoColoringCommandId = 0x0100;

    private static readonly Guid ProviderGuid = new(SqlDatabaseTabGroupProvider.ProviderGuidString);

    // DTE 的事件來源沒有人握著就會被回收，事件安靜地不再發出。
    private static CommandEvents? _noColoringEvents;
    private static Dispatcher? _dispatcher;

    private static ViewManagerPreferences Preferences => ViewManager.Instance.Preferences;

    public static async Task StartAsync(AsyncPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();

        // 認不出「無著色」時整件事不做：否則使用者一關閉就被套回去，再也關不掉。
        if (await package.GetServiceAsync(typeof(DTE)).ConfigureAwait(true) is not DTE dte)
        {
            SqlAssistDiagnostics.WriteAlways("取不到 DTE，重開 SSMS 後不會還原依資料庫著色");
            return;
        }

        _dispatcher = Dispatcher.CurrentDispatcher;
        _noColoringEvents = dte.Events.get_CommandEvents(SwitcherCommandSet, NoColoringCommandId);
        _noColoringEvents.BeforeExecute += OnNoColoringExecuting;

        // 先套用再訂閱：這裡看到的是殼層還沒選過的初始狀態，訂閱在前會被當成使用者改選別的。
        ApplyRemembered();
        Preferences.PropertyChanged += OnPreferencesChanged;
    }

    private static void OnNoColoringExecuting(string guid, int id, object customIn, object customOut, ref bool cancelDefault) =>
        SqlAssistPlatformGuard.Run("記下關閉依資料庫著色", () => SqlAssistState.TabsColoredByDatabase = false);

    private static void OnPreferencesChanged(object sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is not (nameof(ViewManagerPreferences.ColorizeDocumentTabs)
            or nameof(ViewManagerPreferences.CurrentFileGroupProvider)))
        {
            return;
        }

        SqlAssistPlatformGuard.Run("記下頁籤著色方式", () =>
        {
            var preferences = Preferences;
            var step = SqlDatabaseTabChoice.Next(
                SqlAssistState.TabsColoredByDatabase,
                preferences.ColorizeDocumentTabs,
                preferences.CurrentFileGroupProvider == ProviderGuid);

            switch (step)
            {
                case SqlDatabaseTabChoiceStep.Remember:
                    SqlAssistState.TabsColoredByDatabase = true;
                    break;
                case SqlDatabaseTabChoiceStep.Forget:
                    SqlAssistState.TabsColoredByDatabase = false;
                    break;
                case SqlDatabaseTabChoiceStep.Reapply:
                    // 殼層正在逐項套用設定，排到它做完之後，否則它存檔時讀到的是做到一半的狀態。
                    _dispatcher?.BeginInvoke(new Action(() =>
                        SqlAssistPlatformGuard.Run("還原依資料庫著色", ApplyRemembered)));
                    break;
            }
        });
    }

    private static void ApplyRemembered()
    {
        if (!SqlAssistState.TabsColoredByDatabase) return;

        var preferences = Preferences;
        preferences.CurrentFileGroupProvider = ProviderGuid;
        preferences.ColorizeDocumentTabs = true;
    }
}
