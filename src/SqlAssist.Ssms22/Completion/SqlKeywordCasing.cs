using System;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Completion;

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
    /// <summary>
    /// 使用者即將輸入 <paramref name="typedCharacter"/>，先處理它前面那個字。
    /// </summary>
    /// <remarks>
    /// 在字元真的被插入<b>之前</b>改寫：這時要改的字已經完整地在緩衝區裡，
    /// 而且改寫長度與原字相同，游標不會跑掉，接著讓編輯器照常插入該字元。
    /// </remarks>
    public static void ApplyBeforeTypedCharacter(
        ITextView textView,
        ITextBuffer buffer,
        char typedCharacter)
    {
        if (!SqlKeywordCase.IsWordSeparator(typedCharacter))
        {
            return;
        }

        Apply(textView, buffer);
    }

    /// <summary>把游標前的關鍵字改成大寫；不是關鍵字就什麼都不做。</summary>
    public static void Apply(ITextView textView, ITextBuffer buffer)
    {
        if (textView is null || buffer is null || textView.IsClosed)
        {
            return;
        }

        var settings = SqlAssistSettingsStore.Current;

        if (!settings.Enabled || !settings.UppercaseKeywordsOnType)
        {
            return;
        }

        // 有選取範圍時使用者是要取代它，不是在打字。
        if (!textView.Selection.IsEmpty)
        {
            return;
        }

        var caret = textView.Caret.Position.BufferPosition;
        var snapshot = caret.Snapshot;

        if (!ReferenceEquals(snapshot.TextBuffer, buffer))
        {
            return;
        }

        var rewrite = SqlKeywordCase.TryUppercaseWordBefore(
            new SnapshotTextSource(snapshot),
            caret.Position);

        if (rewrite is null)
        {
            return;
        }

        using var edit = buffer.CreateEdit();
        edit.Replace(new Span(rewrite.Start, rewrite.Length), rewrite.Replacement);

        if (edit.Apply() is null || edit.Canceled)
        {
            return;
        }

        SqlAssistDiagnostics.Write($"關鍵字已轉大寫：{rewrite.Replacement}", textView);
    }
}
