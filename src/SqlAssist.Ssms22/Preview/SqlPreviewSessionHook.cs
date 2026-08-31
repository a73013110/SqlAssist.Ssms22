using System;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 把結構預覽接到建議清單的生命週期上。
/// </summary>
/// <remarks>
/// 清單一開，預覽就知道自己的錨點在哪、該在誰結束時收起來；
/// 同時趁這個時間點把視窗先建好——那是使用者剛開始看清單、
/// 還沒決定要不要按向右鍵的空檔，UI 執行緒正好閒著。
/// </remarks>
internal sealed class SqlPreviewSessionHook
{
    private readonly ITextView _textView;
    private readonly IAsyncCompletionBroker _broker;
    private readonly IServiceProvider _serviceProvider;

    private SqlPreviewSessionHook(
        ITextView textView,
        IAsyncCompletionBroker broker,
        IServiceProvider serviceProvider)
    {
        _textView = textView;
        _broker = broker;
        _serviceProvider = serviceProvider;
    }

    public static void Attach(
        ITextView textView,
        IAsyncCompletionBroker? broker,
        IServiceProvider serviceProvider)
    {
        if (textView is null || broker is null)
        {
            return;
        }

        var hook = new SqlPreviewSessionHook(textView, broker, serviceProvider);
        broker.CompletionTriggered += hook.OnCompletionTriggered;
        textView.Closed += hook.OnTextViewClosed;
    }

    private void OnCompletionTriggered(object sender, CompletionTriggeredEventArgs eventArgs)
    {
        // 這是事件處理常式，丟出去就會變成使用者眼前的錯誤對話框。
        SqlAssistPlatformGuard.Run("接上結構預覽", () =>
        {
            // 這個事件是 broker 層級的，其他編輯器的 session 也會通知到這裡。
            if (!ReferenceEquals(eventArgs.TextView, _textView))
            {
                return;
            }

            var settings = SqlAssistSettingsStore.Current;

            // 總開關也要在這裡看一次：這個事件由 broker 發出，來源可能是別的擴充
            // 的建議清單，不保證經過本擴充那條已經檢查過 Enabled 的路徑。
            if (!settings.Enabled || settings.PreviewMode == SqlPreviewMode.Off)
            {
                return;
            }

            if (SqlStructurePreview.GetOrCreate(_textView, _serviceProvider) is not { } preview)
            {
                return;
            }

            // Broker 也會通知 SSMS 原生或其他擴充的清單；先只記住最新候選。
            // 真正的 ownership 等 SqlAssist item 的 callback 帶著同一個 session 回來才建立。
            preview.ObserveSession(eventArgs.CompletionSession);
            preview.Warmup();
        });
    }

    private void OnTextViewClosed(object sender, EventArgs eventArgs)
    {
        _broker.CompletionTriggered -= OnCompletionTriggered;
        _textView.Closed -= OnTextViewClosed;
    }
}
