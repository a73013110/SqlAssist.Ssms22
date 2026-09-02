using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SqlAssist.Ssms22;

/// <summary>
/// SSMS 底部狀態列上的一行字。
/// </summary>
/// <remarks>
/// 這是 F12 那一條路徑唯一的回饋管道：使用者按下去之後要等一次資料庫查詢，
/// 期間畫面上什麼都沒發生，而失敗的三種情形（認不出物件、取不到結構、
/// 開不了視窗）也都必須說得出原因——那些<b>不能</b>交給
/// <see cref="SqlAssistPlatformGuard"/>，它的意思是「這一輪安靜地什麼都不做」。
///
/// 刻意不用對話框：F12 是按鍵路徑，每按一次跳一個要按確定的視窗比沒有回應更糟。
/// </remarks>
internal static class SqlAssistStatusBar
{
    /// <summary>訊息一律標上來源，否則使用者分不出這一行是誰寫的。</summary>
    private const string Prefix = "SqlAssist：";

    public static void Show(IServiceProvider? serviceProvider, string message)
    {
        Write(serviceProvider, statusBar => statusBar.SetText(Prefix + message));
    }

    /// <summary>把狀態列還給 SSMS。</summary>
    /// <remarks>
    /// 用 <c>Clear</c> 而不是把文字設成空字串：前者會還原殼層自己的預設文字
    /// （「就緒」），後者留下一條空白的狀態列。
    /// </remarks>
    public static void Clear(IServiceProvider? serviceProvider)
    {
        Write(serviceProvider, statusBar => statusBar.Clear());
    }

    /// <remarks>
    /// 走 <c>Run</c> 而不是 <c>Probe</c>：取不到狀態列代表使用者按了 F12 卻完全
    /// 沒有回饋，那是要留下完整堆疊的失敗，不是可有可無的平台探測。
    /// </remarks>
    private static void Write(IServiceProvider? serviceProvider, Action<IVsStatusbar> write)
    {
        if (serviceProvider is null)
        {
            return;
        }

        SqlAssistPlatformGuard.Run("更新狀態列", () =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (serviceProvider.GetService(typeof(SVsStatusbar)) is not IVsStatusbar statusBar)
            {
                return;
            }

            // 別人（例如正在執行的查詢）可能凍結了狀態列；沒有先解凍的話
            // 呼叫會回報成功卻什麼都不顯示。
            statusBar.FreezeOutput(0);
            write(statusBar);
        });
    }
}
