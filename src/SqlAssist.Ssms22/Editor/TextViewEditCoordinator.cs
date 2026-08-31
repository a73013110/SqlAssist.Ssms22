using System;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace SqlAssist.Ssms22.Editor;

/// <summary>要寫回編輯器的文字，以及寫進去之後要留下的紀錄。</summary>
internal readonly struct TextReplacement
{
    public TextReplacement(string text, string expansionLabel, string successMessage, int caretOffset = -1)
    {
        Text = text;
        ExpansionLabel = expansionLabel;
        SuccessMessage = successMessage;
        CaretOffset = caretOffset;
    }

    public string Text { get; }

    /// <summary>
    /// 替換後游標要停在 <see cref="Text"/> 的第幾個字元；負值代表停在結尾。
    /// </summary>
    /// <remarks>
    /// 展開成骨架的兩種（INSERT、EXEC）要停在第一個待填的值上——那是使用者
    /// 接下來要做的第一件事，停在結尾等於逼他自己捲回去。整句換成既有定義的
    /// ALTER 沒有這種位置，維持停在結尾。
    /// </remarks>
    public int CaretOffset { get; }

    /// <summary>寫進 <see cref="SqlAssistRuntimeState"/> 的簡短描述，診斷狀態會顯示它。</summary>
    public string ExpansionLabel { get; }

    public string SuccessMessage { get; }
}

/// <summary>
/// 背景取得資料之後，把結果安全地寫回編輯器。
/// </summary>
/// <remarks>
/// ALTER 模組展開與 <c>SELECT *</c> 展開都是「先在背景查資料，回來再改文字」，
/// 而那段路上每一步漏掉都會直接損壞使用者的輸入：沒切回 UI 執行緒就改緩衝區、
/// 沒檢查編輯器已關閉、沒有從 <see cref="ITrackingSpan"/> 取最新範圍、
/// 沒有確認等待期間原文還在原處。兩份各自維護的下場是其中一份少了一道。
///
/// 「要換成什麼」與「原文還算不算數」留給呼叫端：那是各自的商業判斷，
/// 這裡只提供不會弄壞緩衝區的框架。
/// </remarks>
internal sealed class TextViewEditCoordinator
{
    private readonly ITextView _textView;

    public TextViewEditCoordinator(ITextView textView)
    {
        _textView = textView ?? throw new ArgumentNullException(nameof(textView));
    }

    /// <summary>
    /// 切到 UI 執行緒，把追蹤範圍換成 <paramref name="buildReplacement"/> 算出來的文字。
    /// </summary>
    /// <param name="operationName">失敗時寫進紀錄的操作名稱，例如「ALTER 語句」。</param>
    /// <param name="buildReplacement">
    /// 拿到範圍在<b>最新</b>快照上的位置，回傳要替換的內容；回傳 null 代表放棄這次替換。
    /// 「等待期間使用者已經改掉原文」的判斷就在這裡做——放棄的理由各不相同，
    /// 診斷訊息也由呼叫端自己寫。
    /// </param>
    /// <param name="suppressBufferChange">
    /// 替換前後通知呼叫端，讓它暫時忽略自己掛在緩衝區上的變更事件。
    /// </param>
    public void ReplaceTracked(
        ITrackingSpan span,
        string operationName,
        Func<SnapshotSpan, TextReplacement?> buildReplacement,
        Action<bool>? suppressBufferChange = null)
    {
        var dispatcher = ResolveDispatcher();

        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(
                () => ReplaceTracked(span, operationName, buildReplacement, suppressBufferChange)));
            return;
        }

        if (_textView.IsClosed)
        {
            return;
        }

        suppressBufferChange?.Invoke(true);

        try
        {
            // 這裡是從背景工作回到 UI 執行緒後執行的，沒有其他人會接這個例外。
            SqlAssistPlatformGuard.Run($"替換{operationName}", () =>
            {
                var buffer = _textView.TextBuffer;
                var target = span.GetSpan(buffer.CurrentSnapshot);

                if (buildReplacement(target) is not { } replacement)
                {
                    return;
                }

                using var edit = buffer.CreateEdit();
                edit.Replace(target, replacement.Text);
                var updated = edit.Apply();

                // 別人在同一個交易裡否決了這次編輯，緩衝區沒有變，游標也不該動。
                if (edit.Canceled)
                {
                    return;
                }

                var offset = replacement.CaretOffset < 0 || replacement.CaretOffset > replacement.Text.Length
                    ? replacement.Text.Length
                    : replacement.CaretOffset;
                var caret = Math.Min(target.Start.Position + offset, updated.Length);
                _textView.Caret.MoveTo(new SnapshotPoint(updated, caret));
                _textView.Caret.EnsureVisible();
                SqlAssistRuntimeState.MarkExpansion(replacement.ExpansionLabel);
                SqlAssistDiagnostics.WriteAlways(replacement.SuccessMessage, _textView);
            });
        }
        finally
        {
            suppressBufferChange?.Invoke(false);
        }
    }

    private Dispatcher? ResolveDispatcher()
    {
        return _textView is IWpfTextView wpfTextView
            ? wpfTextView.VisualElement.Dispatcher
            : Application.Current?.Dispatcher;
    }
}
