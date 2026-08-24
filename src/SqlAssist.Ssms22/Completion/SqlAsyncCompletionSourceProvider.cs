using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 對 SQL 編輯器匯出探測用的非同步建議來源。
/// </summary>
/// <remarks>
/// 匯出本身不會改變 SSMS 行為：來源預設回報不參與完成，因此不會建立任何 session，
/// 平台的完成命令處理器會直接把按鍵讓給既有的舊版流程。
/// </remarks>
[Export(typeof(IAsyncCompletionSourceProvider))]
[Name("SqlAssist SSMS 22 Async Completion Probe")]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class SqlAsyncCompletionSourceProvider : IAsyncCompletionSourceProvider
{
    private readonly SqlAsyncCompletionSource _source = new();

    public IAsyncCompletionSource GetOrCreate(ITextView textView)
    {
        AsyncCompletionProbe.RecordProviderRequested();
        return _source;
    }
}
