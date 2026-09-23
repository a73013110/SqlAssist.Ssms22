using System;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Ssms22.Editor;

namespace SqlAssist.Ssms22.Connections;

/// <summary>
/// 請 SSMS 開一個空白查詢視窗：沿用目前連線的，或完全沒有連線的。
/// </summary>
/// <remarks>
/// 這是本擴充唯一一處建立文件視窗的地方。編輯器的公開 API 開不出「SSMS 的查詢
/// 視窗」——那是 SSMS 自己的文件類型，帶著連線、資料庫下拉與執行查詢的能力；
/// 用 <c>IVsUIShellOpenDocument</c> 開一份 .sql 只會得到一個沒有連線的文字編輯器。
///
/// 走的是 <c>IScriptFactory</c>，也就是 SSMS 自己的「新增查詢」按鈕走的那一條。
/// 它是註冊在殼層的全域服務，型別在 <c>SqlWorkbench.Interfaces</c> 裡是公開的，
/// 不必碰 <c>ServiceCache</c>（那個類別只是同一次 <c>GetService</c> 的包裝）。
///
/// 這裡向 SSMS 詢問目前連線，是 QuickInfo 路徑明令禁止的動作——那個呼叫有 UI
/// 執行緒相依性。差別在於這條路徑是使用者按 F12 主動觸發的，等得起一次往返，
/// 而且一輪只問一次。
/// </remarks>
internal static class SsmsScriptWindow
{
    /// <summary>
    /// 開一個沿用目前連線的空白查詢視窗並取回它的編輯器。
    /// </summary>
    /// <param name="failure">失敗時要顯示給使用者看的那一句；成功時為空字串。</param>
    /// <returns>新視窗的編輯器；任何一步沒成功時為 null。</returns>
    public static IWpfTextView? TryCreateBlankQuery(IServiceProvider serviceProvider, out string failure)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        failure = string.Empty;

        if (ResolveFactory(serviceProvider) is not { } factory)
        {
            failure = "取不到 SSMS 的查詢視窗服務，無法開啟新視窗。";
            return null;
        }

        // 這一份就是 SSMS「新增查詢」沿用的那組連線資訊。
        var active = factory.CurrentlyActiveWndConnectionInfo;
        var group = active?.UIConnectionGroupInfo;
        var single = active?.UIConnectionInfo;

        if (single is null && (group is null || group.Count == 0))
        {
            failure = "目前的查詢視窗沒有連線，無法沿用連線開啟新視窗。";
            return null;
        }

        // 分成兩支與 SSMS 自己的「新增查詢（沿用目前連線）」逐字一致：
        // 多重伺服器連線的視窗要帶整組過去，只帶第一個會安靜地少連幾台。
        //
        // 第三個參數是「直接沿用這條實際連線」。一律傳 null，讓新視窗用同一組
        // 認證另開一條——共用同一條連線代表兩個視窗共用一個 SPID，
        // 一邊執行長查詢另一邊就卡住。
        //
        // ScriptType 寫死 Sql 而不是沿用 active.ScriptType：這份指令碼是 T-SQL，
        // 跟著一個 MDX 視窗開出 MDX 編輯器只會得到一個貼不進去的視窗。
        return Capture(() => group is { Count: > 0 }
            ? factory.CreateNewBlankScript(ScriptType.Sql, group, null)
            : factory.CreateNewBlankScript(ScriptType.Sql, single, null), out failure);
    }

    /// <summary>
    /// 開一個<b>沒有連線</b>的空白查詢視窗並取回它的編輯器；第一次執行時由 SSMS 要求連線。
    /// </summary>
    /// <param name="failure">失敗時要顯示給使用者看的那一句；成功時為空字串。</param>
    /// <returns>新視窗的編輯器；任何一步沒成功時為 null。</returns>
    /// <remarks>
    /// 給「這份東西不在查詢視窗那一台上」用：沿用連線等於在錯的伺服器上按 F5，而 SSMS 沒有
    /// 公開的方法把物件總管那條連線換成查詢視窗要的 <c>UIConnectionInfo</c>。
    ///
    /// <b>只傳 null 連線不夠。</b>空的連線資訊只是不蓋連線戳記；編輯器工廠建立視窗時另外看
    /// <see cref="IScriptFactory.OpenFileMode"/>，預設的 <c>Connected</c> 會把作用中視窗那條
    /// 連線設給新視窗並自動連上——正是要避開的那一台，而且畫面上看不出來。<c>Prompt</c> 則是
    /// 一開窗就跳連線對話框。只有 <c>Disconnected</c> 讓它不連也不問，與 SSMS 自己
    /// 「開啟檔案（不連線）」同一招。
    ///
    /// 那是工廠上的共用狀態，所以只在這一次呼叫期間改，結束後<b>還原成原本那一個值</b>，
    /// 不寫回 <c>Connected</c>：SSMS 的「新增查詢」會把它設成 <c>Prompt</c> 之後不還原，
    /// 寫回固定值等於替使用者改掉他剛留下的狀態。
    /// </remarks>
    public static IWpfTextView? TryCreateUnconnectedQuery(IServiceProvider serviceProvider, out string failure)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        failure = string.Empty;

        if (ResolveFactory(serviceProvider) is not { } factory)
        {
            failure = "取不到 SSMS 的查詢視窗服務，無法開啟新視窗。";
            return null;
        }

        return Capture(() =>
        {
            var previous = factory.OpenFileMode;
            factory.OpenFileMode = OpenFileModeEnum.Disconnected;

            try
            {
                return factory.CreateNewBlankScript(ScriptType.Sql, (UIConnectionInfo?)null, null);
            }
            finally
            {
                factory.OpenFileMode = previous;
            }
        }, out failure);
    }

    /// <summary>建立視窗並只取回這一次建立的那一個編輯器；兩種視窗共用。</summary>
    private static IWpfTextView? Capture(Func<object?> create, out string failure)
    {
        failure = string.Empty;
        object? document = null;

        var created = ActiveSqlEditor.CaptureCreated(() => document = create());

        if (created is not null)
        {
            return created;
        }

        // 分成兩句話：視窗開出來了卻取不到編輯器，跟根本沒開出視窗，
        // 後續要看的地方完全不同。工廠回傳的是 SSMS 自己的文件檢視型別，
        // 本擴充從它身上拿不到 IWpfTextView，所以只拿來判斷開了沒有。
        failure = document is null
            ? "SSMS 沒有建立出新的查詢視窗。"
            : "已開啟新的查詢視窗，但取不到它的編輯器，定義沒有寫入。";

        SqlAssistDiagnostics.WriteAlways($"開啟查詢視窗失敗：{failure}");
        return null;
    }

    /// <remarks>
    /// 先問套件自己的服務提供者，再退回全域服務——與 SSMS 的 <c>ServiceCache</c>
    /// 同一個順序。第一支在殼層還沒把 SqlAssist 完全 site 好時會落空，
    /// 而那正是使用者剛開 SSMS 就按 F12 的那一次。
    /// </remarks>
    private static IScriptFactory? ResolveFactory(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return serviceProvider.GetService(typeof(IScriptFactory)) as IScriptFactory
            ?? Package.GetGlobalService(typeof(IScriptFactory)) as IScriptFactory;
    }
}
