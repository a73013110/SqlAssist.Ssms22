using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.PlatformUI.Packages.FileColor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SqlAssist.Core.Connections;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Connections;

/// <summary>
/// 殼層「文件索引標籤著色方式」的「資料庫」：同一個伺服器＋資料庫的查詢視窗同一色。
/// </summary>
/// <remarks>
/// 掛在殼層的著色服務上，不自己畫頁籤：選單項目、開關、配色與「選取索引標籤色彩」都由殼層提供，
/// 所以不另立設定。殼層只有在使用者選了這一項時才建立這個物件，沒選的人不多付任何成本。
/// </remarks>
internal sealed class SqlDatabaseTabGroupProvider : IFileGroupProvider
{
    /// <summary>殼層照這個 GUID 的字典順序排選單，開頭要大於內建的規則運算式才會排在最後。</summary>
    public const string ProviderGuidString = "fa590269-cd98-4002-96d4-a9150abfc0e0";

    private readonly object _syncRoot = new();
    private SqlDatabaseTabGroups? _groups;

    private SqlDatabaseTabGroupProvider(IServiceProvider serviceProvider)
    {
        SqlEditorConnectionWatcher.Changed += OnGroupingInputChanged;
        SqlAssistSettingsStore.Changed += OnGroupingInputChanged;

        // 沒有編輯器時事件也要接上：連線事件比第一份中繼資料服務早發的話，
        // 那個頁籤問的當下還沒連線，之後也沒有人叫它重問，就一直沒有顏色。
        SqlEditorConnectionWatcher.EnsureSubscribed(serviceProvider);
    }

    /// <remarks>殼層拿這句對顯示名稱做 <c>string.Format</c>，所以佔位符要原樣留成 {0}。</remarks>
    public string TooltipPhrase => ConnectionText.DatabaseTabTooltip("{0}");

    public event EventHandler<GroupingChangedEventArgs>? GroupingChanged;

    /// <summary>必須在套件初始化完成前呼叫：殼層載入套件之後就來取，取不到會當成沒有這個著色方式。</summary>
    public static async Task RegisterAsync(AsyncPackage package)
    {
        if (await package.GetServiceAsync(typeof(SRegisterFileGroupProvider)).ConfigureAwait(false)
            is not IRegisterFileGroupProvider registrar)
        {
            SqlAssistDiagnostics.WriteAlways("取不到殼層的頁籤著色服務，依資料庫著色不會生效");
            return;
        }

        registrar.RegisterProvider(
            new Guid(ProviderGuidString),
            () => Task.FromResult<IFileGroupProvider>(new SqlDatabaseTabGroupProvider(package)));

        await SqlDatabaseTabChoiceMemory.StartAsync(package).ConfigureAwait(false);
    }

    public async Task<int?> GetFileGroupIdAsync(string filePath, IVsHierarchy hierarchy, uint itemId, CancellationToken token)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
        return SqlAssistPlatformGuard.Probe<int?>("查詢頁籤所屬的資料庫", () => GroupIdOf(filePath), null);
    }

    public string GetFileGroupDisplayName(int groupId)
    {
        lock (_syncRoot)
        {
            return _groups?.DisplayName(groupId) ?? string.Empty;
        }
    }

    /// <returns>沒有連線的查詢視窗與不是查詢視窗的文件都是 null，殼層就不著色。</returns>
    /// <remarks>
    /// 每次都問 SSMS，不讀 <see cref="SqlWindowConnections.Get"/>：那份只記事件來過的視窗，
    /// 而且識別碼會被重複使用，關掉重開的未連線視窗會拿到上一個視窗的顏色。
    /// </remarks>
    private int? GroupIdOf(string moniker)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!SqlAssistSettingsStore.Current.Enabled || SqlWindowConnections.ReadWindow(moniker) is not { } label)
        {
            return null;
        }

        lock (_syncRoot)
        {
            _groups ??= SqlDatabaseTabGroups.Parse(SqlAssistState.DatabaseTabGroups);
            var id = _groups.IdOf(label, out var added);

            if (added)
            {
                SqlAssistState.DatabaseTabGroups = _groups.Format();
            }

            return id;
        }
    }

    /// <remarks>
    /// 只用 Update：Add 會讓殼層重讀使用者改過的顏色、Remove 會刪掉它，這裡兩者都沒有發生。
    /// 收到之後殼層讓每個頁籤重問一次，編號為 0 只是佔位；每批 F5 都走一次，每個頁籤只查記憶體。
    /// </remarks>
    private void OnGroupingInputChanged(object? sender, EventArgs eventArgs) =>
        GroupingChanged?.Invoke(this, new GroupingChangedEventArgs(0, GroupingChangedAction.Update));
}
