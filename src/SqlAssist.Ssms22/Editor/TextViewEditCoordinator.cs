using System;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Diagnostics;

namespace SqlAssist.Ssms22.Editor;

/// <summary>要寫回編輯器的文字，以及寫進去之後要留下的紀錄。</summary>
internal readonly struct TextReplacement
{
    public TextReplacement(
        string text,
        SqlAssistActivityKind activityKind,
        string successMessage,
        int caretOffset = -1,
        int affectedItemCount = 0)
    {
        Text = text;
        ActivityKind = activityKind;
        SuccessMessage = successMessage;
        CaretOffset = caretOffset;
        AffectedItemCount = affectedItemCount;
    }

    public string Text { get; }

    /// <summary>
    /// 替換後游標要停在 <see cref="Text"/> 的第幾個字元；負值代表停在結尾。
    /// </summary>
    /// <remarks>
    /// 三種展開都不停在結尾——停在結尾等於一展開就被捲到最後一行，使用者得自己捲回去。
    /// 展開成骨架的兩種（INSERT、EXEC）停在第一個待填的值上，那是他接下來要做的第一件事；
    /// 整句換成既有定義的 ALTER 停在標頭的物件名稱之後，那是他讀一份定義的起點。
    /// </remarks>
    public int CaretOffset { get; }

    /// <summary>
    /// 寫進 <see cref="SqlAssistRuntimeState"/> 的動作種類；不用任意文字，
    /// 才不會把資料庫物件名稱或 SQL 內容帶進可公開貼出的診斷摘要。
    /// </summary>
    public SqlAssistActivityKind ActivityKind { get; }

    public int AffectedItemCount { get; }

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
    private readonly ITextBuffer _buffer;

    public TextViewEditCoordinator(ITextView textView, ITextBuffer? buffer = null)
    {
        _textView = textView ?? throw new ArgumentNullException(nameof(textView));
        _buffer = buffer ?? textView.TextBuffer;
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
                var buffer = _buffer;
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

                Complete(updated, target.Start.Position, replacement);
            });
        }
        finally
        {
            suppressBufferChange?.Invoke(false);
        }
    }

    /// <summary>
    /// 把整份文字寫進一個還是空的緩衝區。
    /// </summary>
    /// <remarks>
    /// 用在「另開一個查詢視窗顯示物件定義」：目的地是剛建立、還沒有任何內容的
    /// 編輯器，沒有要追蹤的範圍，也沒有等待期間會被使用者改掉的原文。
    /// 對應 <see cref="ReplaceTracked"/> 那一道「原文還在原處」的守門，
    /// 這裡是<b>緩衝區必須還是空的</b>：認錯視窗就是把指令碼蓋到使用者正在編輯的
    /// 查詢上，而那是這個類別存在的唯一理由。
    ///
    /// 與 <see cref="ReplaceTracked"/> 的另外兩點差別，各有理由：
    /// <list type="bullet">
    /// <item>不自己切執行緒。呼叫端剛在 UI 執行緒上建立這個編輯器，
    /// 不可能從別處拿到它；真的不在 UI 執行緒就是程式錯誤，要看得見。</item>
    /// <item>不收斂例外。這條路徑是使用者按 F12 觸發的，安靜地什麼都不做
    /// 等於故障；失敗要冒到呼叫端去顯示訊息。</item>
    /// </list>
    /// </remarks>
    /// <returns>寫進去了為 true；緩衝區不是空的或編輯器已關閉為 false。</returns>
    public bool InsertIntoBlank(TextReplacement replacement)
    {
        if (ResolveDispatcher() is { } dispatcher && !dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("InsertIntoBlank 只能在 UI 執行緒上呼叫。");
        }

        if (_textView.IsClosed)
        {
            return false;
        }

        var buffer = _buffer;

        if (buffer.CurrentSnapshot.Length != 0)
        {
            return false;
        }

        using var edit = buffer.CreateEdit();
        edit.Insert(0, replacement.Text);
        var updated = edit.Apply();

        if (edit.Canceled)
        {
            return false;
        }

        Complete(updated, 0, replacement);
        return true;
    }

    /// <summary>
    /// 編輯完成之後共通的收尾：游標落點、活動紀錄與診斷訊息。
    /// </summary>
    /// <remarks>
    /// 兩條寫入路徑只有「寫什麼」不同，收尾完全一樣。各寫一份的下場是其中一份
    /// 忘了 <c>EnsureVisible</c> 或忘了記活動，而那不會有任何徵兆。
    /// </remarks>
    private void Complete(ITextSnapshot updated, int start, TextReplacement replacement)
    {
        var offset = replacement.CaretOffset < 0 || replacement.CaretOffset > replacement.Text.Length
            ? replacement.Text.Length
            : replacement.CaretOffset;
        var caret = Math.Min(start + offset, updated.Length);
        var caretPoint = new SnapshotPoint(updated, caret);
        var viewPoint = ReferenceEquals(updated.TextBuffer, _textView.TextBuffer)
            ? caretPoint
            : _textView.BufferGraph.MapUpToBuffer(
                caretPoint,
                PointTrackingMode.Positive,
                PositionAffinity.Successor,
                _textView.TextBuffer);

        if (viewPoint is { } mapped)
        {
            _textView.Caret.MoveTo(mapped);
        }

        _textView.Caret.EnsureVisible();
        SqlAssistRuntimeState.MarkActivity(replacement.ActivityKind, replacement.AffectedItemCount);
        SqlAssistDiagnostics.WriteAlways(replacement.SuccessMessage, _textView);
    }

    private Dispatcher? ResolveDispatcher()
    {
        return _textView is IWpfTextView wpfTextView
            ? wpfTextView.VisualElement.Dispatcher
            : Application.Current?.Dispatcher;
    }
}
