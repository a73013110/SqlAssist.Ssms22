using System;
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
    public static void AfterCurrentCommand(ITextView? textView, string operation, Action<ITextView> work)
    {
        if (textView is null || textView.IsClosed)
        {
            return;
        }

        var dispatcher = (textView as IWpfTextView)?.VisualElement.Dispatcher
            ?? Dispatcher.CurrentDispatcher;

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
