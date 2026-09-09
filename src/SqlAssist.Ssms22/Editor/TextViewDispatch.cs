using System;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Editor;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 把工作排到「這一輪命令結束之後」再做。
/// </summary>
/// <remarks>
/// 三個時機需要它，原因都一樣：<b>現在的文字與 session 狀態都還沒定案</b>。
///
/// <list type="bullet">
/// <item>提交建議時——平台正要把 completion session 收掉。</item>
/// <item>輸入字元時——那個字元還沒進緩衝區。</item>
/// <item>啟動原生 Snippet 時——同上，而且引擎會在自己的呼叫堆疊裡回呼我們。</item>
/// </list>
///
/// 在原地做的話，看到的是上一個狀態；重開的清單、算出來的範圍都是錯的。
/// 一律排到 <see cref="DispatcherPriority.Background"/>：比輸入與繪製都低，
/// 使用者連續打字時不會插隊。
/// </remarks>
internal static class TextViewDispatch
{
    /// <remarks>
    /// 呼叫端可能已經在背景（查完中繼資料才回頭排一件 UI 的事），所以先取派送器再問
    /// 別的：<c>textView.IsClosed</c> 那一問留給排進去之後在 UI 執行緒上做，
    /// 免得在背景執行緒上碰編輯器。
    ///
    /// 取不到派送器就整件事不做。這裡曾經退回 <c>Dispatcher.CurrentDispatcher</c>，
    /// 而那在背景執行緒上會<b>當場建一個沒有人抽的佇列</b>——工作排進去之後永遠不會執行，
    /// 而且一行紀錄都沒有。
    /// </remarks>
    public static void AfterCurrentCommand(ITextView? textView, string operation, Action<ITextView> work)
    {
        if (textView is null)
        {
            return;
        }

        var dispatcher = (textView as IWpfTextView)?.VisualElement.Dispatcher
            ?? Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            // 這是排進派送佇列的工作，丟出去就是使用者眼前的錯誤對話框。
            new Action(() => SqlAssistPlatformGuard.Run(
                operation,
                () =>
                {
                    // 排隊期間使用者可能已經把查詢視窗關掉了。
                    if (!textView.IsClosed)
                    {
                        work(textView);
                    }
                })));
    }
}
