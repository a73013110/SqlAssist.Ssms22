using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Wildcards;

/// <summary>
/// 游標停在展得開的 <c>*</c> 後方時，在旁邊提示可以按 Tab。
/// </summary>
/// <remarks>
/// 這個功能沒有提示等於不存在：使用者不會憑空去試按 Tab，而按下去之前也看不出
/// 這一次到底展不展得開。提示出現與否就是 <see cref="SqlWildcardExpander.Find"/>
/// 那份判斷的結果，與 Tab 走同一條路，看得到就一定按得動。
/// 什麼時候重新判斷、怎麼顯示與收起交給 <see cref="CaretHint"/>。
/// </remarks>
internal static class SqlWildcardHint
{
    public static void Attach(
        IWpfTextView textView,
        IAsyncCompletionBroker? broker,
        IToolTipPresenterFactory? presenterFactory)
    {
        CaretHint.Attach(textView, broker, presenterFactory, "更新萬用字元提示", Evaluate);
    }

    private static CaretHintContent? Evaluate(SnapshotPoint caret)
    {
        return SqlWildcardExpander.Find(caret.Snapshot, caret.Position, SqlAssistSettingsStore.Current) is { } target
            ? new CaretHintContent(new SnapshotSpan(caret.Snapshot, target.Start, target.Length), WildcardText.TabHint)
            : null;
    }
}
