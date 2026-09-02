using System;
using System.Threading;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Completion;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 把建議清單重開一次。
/// </summary>
/// <remarks>
/// 兩個地方需要它，原因是同一個：平台只在「沒有 session」時才回頭問建議來源，
/// 而這兩個時機的上下文都已經換掉了，舊 session 手上的清單是錯的。
///
/// <list type="bullet">
/// <item>設定了接續建議的 caret 片段展開之後，重新依插入文字收斂清單。</item>
/// <item>輸入結束詞元的字元之後——<c>a.</c> 接著要列 <c>a</c> 的欄位，
/// <c>FROM </c> 接著只列資料表與檢視。</item>
/// </list>
///
/// 提交時回報的 <see cref="CommitBehavior.Retrigger"/> 幫不上忙：SSMS 22 的
/// 編輯器組件裡沒有任何一處讀它。Enter 與 Tab 的處理常式只測
/// <see cref="CommitBehavior.RaiseFurtherReturnKeyAndTabKeyCommandHandlers"/>，
/// 輸入字元的處理常式只測
/// <see cref="CommitBehavior.SuppressFurtherTypeCharCommandHandlers"/>，
/// 那個旗標在這個版本上是死的。
///
/// 一律排到這一輪命令之後再做，不在原地直接呼叫：兩個時機的文字都還沒定案。
/// 提交當下平台正要把 session 收掉，而輸入字元當下那個字元還沒進緩衝區——
/// 在原地開出來的清單，看到的是上一個狀態。排程本身見
/// <see cref="Editor.TextViewDispatch"/>，那裡還有第三個消費者。
/// </remarks>
internal static class SqlCompletionReopen
{
    /// <summary>片段展開之後的接續清單。</summary>
    public static void AfterExpansion(ITextView? textView, IAsyncCompletionBroker? broker)
    {
        if (broker is null)
        {
            return;
        }

        Schedule(textView, view =>
        {
            SqlAssistDiagnostics.Write("片段展開後重開建議清單");
            Reopen(view, broker);
        });
    }

    /// <summary>
    /// 輸入結束詞元的字元之後的清單。
    /// </summary>
    /// <remarks>
    /// 由呼叫端先用 <see cref="SqlCompletionTriggers.MayChangeContext"/> 擋掉不可能
    /// 換掉上下文的字元，這裡再問一次
    /// <see cref="SqlCompletionTriggers.ShouldReopen"/>——那一份判斷要看的是
    /// 字元<b>已經進入緩衝區之後</b>的文字，所以只能在這裡問。
    /// </remarks>
    public static void AfterSeparator(ITextView? textView, IAsyncCompletionBroker? broker)
    {
        if (broker is null)
        {
            return;
        }

        Schedule(textView, view =>
        {
            var caret = view.Caret.Position.BufferPosition;

            // 只取游標前方那一段：判斷用不到後面的文字，而分隔字元按得很勤，
            // 每一次都把整份指令碼複製一遍在大檔案上是白付的代價。
            if (!SqlCompletionTriggers.ShouldReopen(caret.Snapshot.GetText(0, caret.Position)))
            {
                return;
            }

            SqlAssistDiagnostics.Write("上下文已收斂，重開建議清單");
            Reopen(view, broker);
        });
    }

    /// <summary>
    /// 排到這一輪命令之後，而且只有在建議功能還開著時才做。
    /// </summary>
    /// <remarks>
    /// 設定要在<b>執行時</b>問而不是排程時：使用者可能在這中間把建議關掉。
    /// </remarks>
    private static void Schedule(ITextView? textView, Action<ITextView> work)
    {
        TextViewDispatch.AfterCurrentCommand(textView, "重開建議清單", view =>
        {
            var settings = SqlAssistSettingsStore.Current;

            if (settings.Enabled && settings.SuggestionsEnabled)
            {
                work(view);
            }
        });
    }

    /// <summary>
    /// 收掉現有的 session，在游標當下的位置開一個新的並顯示出來。
    /// </summary>
    /// <remarks>
    /// 三個步驟一個都不能少：
    ///
    /// <list type="number">
    /// <item>
    /// 先收掉舊的。<c>TriggerCompletion</c> 一開頭就先問 <c>GetSession</c>，
    /// 只要還有 session 就原封不動把它交回來——不先收掉等於整個呼叫沒有作用。
    /// <c>Dismiss</c> 是同步的，回傳前就已經把自己從 broker 的紀錄裡拿掉。
    /// </item>
    /// <item>
    /// <c>TriggerCompletion</c> 只是<b>建立</b> session：它問過各個來源要不要參與、
    /// 算出適用範圍，然後就結束了。
    /// </item>
    /// <item>
    /// <c>OpenOrUpdate</c> 才會去要清單並把 UI 畫出來。少了這一行，前面每一步
    /// 都算對了，畫面上仍然什麼都不會出現——平台自己的命令處理常式在同一個
    /// 位置也是這樣接著寫的。
    /// </item>
    /// </list>
    /// </remarks>
    private static void Reopen(ITextView textView, IAsyncCompletionBroker broker)
    {
        broker.GetSession(textView)?.Dismiss();

        var caret = textView.Caret.Position.BufferPosition;
        var trigger = new CompletionTrigger(CompletionTriggerReason.Insertion, caret.Snapshot);
        var session = broker.TriggerCompletion(textView, trigger, caret, CancellationToken.None);

        session?.OpenOrUpdate(trigger, caret, CancellationToken.None);
    }
}
