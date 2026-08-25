using System;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 本擴充的建議清單開起來時，把 SSMS 內建的 T-SQL IntelliSense 清單收掉。
/// </summary>
/// <remarks>
/// SSMS 內建的 IntelliSense 是舊版語言服務，由它自己的命令篩選器觸發，不會因為
/// 有新版建議來源就讓位。兩份清單同時存在時，舊版語言服務會在退格之類的編輯上
/// 對著已經被換掉的狀態算範圍，於是每刪一個字就跳一次「值未落在預期的範圍內。」。
///
/// 先前只有自製 WPF 清單那條路會關掉它，改用平台原生管線之後
/// <c>suppressNativeIntelliSense</c> 就成了不會生效的設定。
///
/// 只在本擴充的 session <b>剛被觸發的那一刻</b>關一次，不在每一次按鍵上關：
/// 每按一次就呼叫 <see cref="ICompletionBroker.DismissAllSessions"/> 會在舊版語言服務
/// 還在計算時把 session 抽掉，那本身就是同一個錯誤對話框的成因。
/// </remarks>
internal sealed class NativeIntelliSenseSuppressor
{
    private readonly ITextView _textView;
    private readonly ICompletionBroker _legacyBroker;
    private readonly IAsyncCompletionBroker _asyncBroker;

    private NativeIntelliSenseSuppressor(
        ITextView textView,
        ICompletionBroker legacyBroker,
        IAsyncCompletionBroker asyncBroker)
    {
        _textView = textView;
        _legacyBroker = legacyBroker;
        _asyncBroker = asyncBroker;
    }

    /// <summary>掛上抑制器；缺少任一個 broker 時安靜地什麼都不做。</summary>
    public static void Attach(
        ITextView textView,
        ICompletionBroker? legacyBroker,
        IAsyncCompletionBroker? asyncBroker)
    {
        if (textView is null || legacyBroker is null || asyncBroker is null)
        {
            return;
        }

        var suppressor = new NativeIntelliSenseSuppressor(textView, legacyBroker, asyncBroker);
        asyncBroker.CompletionTriggered += suppressor.OnCompletionTriggered;
        textView.Closed += suppressor.OnTextViewClosed;
    }

    private void OnCompletionTriggered(object sender, CompletionTriggeredEventArgs eventArgs)
    {
        try
        {
            // 這個事件是 broker 層級的，其他編輯器的 session 也會通知到這裡。
            if (!ReferenceEquals(eventArgs.TextView, _textView))
            {
                return;
            }

            var settings = SettingsService.Default.GetSnapshot();

            if (!settings.Enabled ||
                !settings.Suggestions.Enabled ||
                !settings.Suggestions.SuppressNativeIntelliSense ||
                settings.Suggestions.Engine != CompletionEngine.Native)
            {
                return;
            }

            if (!_legacyBroker.IsCompletionActive(_textView))
            {
                return;
            }

            _legacyBroker.DismissAllSessions(_textView);
            SqlAssistDiagnostics.Write("已關閉 SSMS 內建的 IntelliSense 清單", _textView);
        }
        catch (Exception exception)
        {
            // 這是事件處理常式，丟出去就會變成使用者眼前的錯誤對話框。
            SqlAssistDiagnostics.WriteAlways($"關閉 SSMS 內建清單失敗：{exception.Message}");
        }
    }

    private void OnTextViewClosed(object sender, EventArgs eventArgs)
    {
        _asyncBroker.CompletionTriggered -= OnCompletionTriggered;
        _textView.Closed -= OnTextViewClosed;
    }
}
