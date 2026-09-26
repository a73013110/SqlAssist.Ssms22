using System;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Keywords;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.Snippets;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 把游標前剛打完的關鍵字改寫成大寫。
/// </summary>
/// <remarks>
/// 在按鍵路徑上，所以順序刻意由便宜到昂貴：
/// 先看這個字元是不是分隔字元（一次比較），再往回讀那個字（幾個字元），
/// 確定是關鍵字之後才去做需要掃過整份文字的語彙狀態判斷。
/// 打字時絕大多數按鍵在第一步就結束。
/// </remarks>
internal static class SqlKeywordCasing
{
    /// <summary>Enter 在這裡的代表字元：換行一樣結束了前一個字。</summary>
    public const char NewLine = '\n';

    /// <summary>
    /// 使用者即將輸入 <paramref name="separator"/>（Enter 是 <see cref="NewLine"/>），
    /// 先處理它前面那個字。
    /// </summary>
    /// <remarks>
    /// TypeChar 與 Enter 共用這一個入口，規則只有一份：分隔字元結束了一個關鍵字就改寫。
    ///
    /// 在字元真的被插入<b>之前</b>改寫：這時要改的字已經完整地在緩衝區裡，
    /// 而且改寫長度與原字相同，游標不會跑掉，接著讓編輯器照常插入該字元。
    ///
    /// Enter 多一個分支：建議清單開著時，Enter 可能是提交鍵。硬選時平台會用選中的項目
    /// 換掉這個字，先改寫大寫只是多一個馬上被蓋掉的復原步驟；軟選時平台關掉清單、
    /// 把 Enter 交給編輯器換行，那時才該改寫。本處理常式與平台的清單處理常式都排在
    /// <c>default</c> 之前，誰先誰後沒有保證，而在這一刻問不出清單是軟選還是硬選
    /// （要等平台算完清單）。所以清單開著時改到這一輪命令之後：那時提交或換行都已經
    /// 發生，字還在原處就改寫，被提交換掉了就不動。平台先處理的那一種順序裡，
    /// 硬選的 Enter 根本不會傳到這裡，軟選的傳到這裡時清單已經關了，走一般的路——
    /// 兩種順序的結果相同。
    ///
    /// TypeChar 不必這樣繞：建議清單沒有任何提交字元，打字不會提交。
    ///
    /// 欄位 session 開著時暫停只寫在這裡，TypeChar 與 Enter 兩個呼叫端都不必再問：
    /// 這是 Snippet Engine 之外的緩衝區編輯，會打斷欄位標記或同名欄位同步。
    /// </remarks>
    public static void ApplyBeforeSeparator(
        ITextView textView,
        ITextBuffer buffer,
        char separator,
        IAsyncCompletionBroker? broker)
    {
        if (!SqlKeywordCase.IsWordSeparator(separator) ||
            !CanRewrite(textView, buffer) ||
            IsSnippetSessionActive(textView))
        {
            return;
        }

        if (separator == NewLine && broker?.GetSession(textView) is not null)
        {
            ApplyAfterCurrentCommand(textView, buffer, separator);
            return;
        }

        var caret = textView.Caret.Position.BufferPosition;

        if (ReferenceEquals(caret.Snapshot.TextBuffer, buffer) &&
            FindRewrite(caret.Snapshot, caret.Position, separator) is { } rewrite)
        {
            Apply(textView, buffer, new Span(rewrite.Start, rewrite.Length), rewrite.Replacement);
        }
    }

    /// <summary>記下游標前那個字，等這一輪命令結束之後再改寫。</summary>
    /// <remarks>
    /// 追蹤範圍用 <see cref="SpanTrackingMode.EdgeExclusive"/>：換行插在字的尾端，
    /// 範圍不能把它吃進去；提交換掉整個字時範圍會縮掉或變成別的字，文字對不上就放棄。
    ///
    /// 要不要改寫只在這裡判一次：之後追蹤範圍的原文還在、後面接的仍是分隔字元，
    /// 就還是同一個字，改寫結果照用，不再掃一次整份文字。
    /// </remarks>
    private static void ApplyAfterCurrentCommand(ITextView textView, ITextBuffer buffer, char separator)
    {
        var caret = textView.Caret.Position.BufferPosition;
        var snapshot = caret.Snapshot;

        if (!ReferenceEquals(snapshot.TextBuffer, buffer) ||
            FindRewrite(snapshot, caret.Position, separator) is not { } rewrite)
        {
            return;
        }

        var word = snapshot.CreateTrackingSpan(rewrite.Start, rewrite.Length, SpanTrackingMode.EdgeExclusive);
        var original = snapshot.GetText(rewrite.Start, rewrite.Length);

        TextViewDispatch.AfterCurrentCommand(textView, "Enter 之後的關鍵字大寫", view =>
        {
            if (IsSnippetSessionActive(view) || !CanRewrite(view, buffer))
            {
                return;
            }

            var current = word.GetSpan(buffer.CurrentSnapshot);
            var end = current.End.Position;

            // 字被提交換掉了，或者它後面又接上了別的字元，那已經不是當初那個字。
            if (!string.Equals(current.GetText(), original, StringComparison.Ordinal) ||
                (end < current.Snapshot.Length && !SqlKeywordCase.IsWordSeparator(current.Snapshot[end])))
            {
                return;
            }

            Apply(view, buffer, current.Span, rewrite.Replacement);
        });
    }

    private static bool IsSnippetSessionActive(ITextView textView) =>
        SqlSnippetExpansionController.Peek(textView)?.HasActiveSession == true;

    private static bool CanRewrite(ITextView textView, ITextBuffer buffer)
    {
        if (textView is null || buffer is null || textView.IsClosed)
        {
            return false;
        }

        var settings = SqlAssistSettingsStore.Current;

        // 有選取範圍時使用者是要取代它，不是在打字。
        return settings.Enabled &&
            settings.UppercaseKeywordsOnType &&
            textView.Selection.IsEmpty;
    }

    /// <summary><paramref name="position"/> 前面的字是關鍵字時，回傳改成大寫的方式；否則 null。</summary>
    /// <param name="separator">
    /// 結束這個字的分隔字元。內建函式要看到左括號才算數，理由見 <see cref="SqlKeywordCase"/>。
    /// </param>
    private static SqlKeywordRewrite? FindRewrite(ITextSnapshot snapshot, int position, char separator) =>
        SqlKeywordCase.TryUppercaseWordBefore(new SnapshotTextSource(snapshot), position, separator);

    /// <summary>把 <paramref name="word"/> 換成 <paramref name="replacement"/>；長度相同，游標不動。</summary>
    private static void Apply(ITextView textView, ITextBuffer buffer, Span word, string replacement)
    {
        using var edit = buffer.CreateEdit();
        edit.Replace(word, replacement);

        if (edit.Apply() is null || edit.Canceled)
        {
            return;
        }

        SqlAssistDiagnostics.Write($"關鍵字已轉大寫：{replacement}", textView);
    }
}
