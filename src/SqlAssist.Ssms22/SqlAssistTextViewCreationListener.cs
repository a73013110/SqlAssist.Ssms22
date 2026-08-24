using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Ssms22.Completion;

namespace SqlAssist.Ssms22;

[Export(typeof(IWpfTextViewCreationListener))]
[Name("SqlAssist SSMS 22 View Diagnostics")]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class SqlAssistTextViewCreationListener : IWpfTextViewCreationListener
{
    [Import(typeof(SVsServiceProvider))]
    internal IServiceProvider ServiceProvider { get; set; } = null!;

    [Import]
    internal ICompletionBroker CompletionBroker { get; set; } = null!;

    /// <summary>
    /// 新版非同步 IntelliSense 的 broker，只用於量測平台是否支援 SQL 內容類型。
    /// 允許缺席：SSMS 若沒有匯出這個實作，整個 MEF part 不可以因此組合失敗，
    /// 否則建議控制器會跟著失效。
    /// </summary>
    [Import(AllowDefault = true)]
    internal IAsyncCompletionBroker? AsyncCompletionBroker { get; set; }

    public void TextViewCreated(IWpfTextView textView)
    {
        SqlAssistRuntimeState.MarkTextViewCreated();
        ActiveSqlEditor.Track(textView); // 工具選單的命令需要知道游標在哪個編輯器。
        RecordAsyncCompletionSupport(textView);
        var controller = new SqlCompletionController(textView, ServiceProvider, CompletionBroker);
        CompletionSessionRegistry.Register(textView, controller);
        SqlAssistDiagnostics.WriteAlways("SQL 編輯器已建立，SqlAssist 建議控制器已載入", textView);
    }

    private void RecordAsyncCompletionSupport(IWpfTextView textView)
    {
        if (AsyncCompletionBroker is null)
        {
            AsyncCompletionProbe.RecordBrokerMissing();
            SqlAssistDiagnostics.WriteAlways("SSMS 沒有匯出 IAsyncCompletionBroker，非同步 IntelliSense 不可用");
            return;
        }

        try
        {
            var contentType = textView.TextBuffer.ContentType;
            var supported = AsyncCompletionBroker.IsCompletionSupported(contentType);
            AsyncCompletionProbe.RecordBrokerSupport(contentType.TypeName, supported);
            SqlAssistDiagnostics.WriteAlways(
                $"非同步 IntelliSense 支援狀態：{contentType.TypeName} → {supported}");
        }
        catch (Exception exception)
        {
            // 量測失敗不可影響編輯器；記下原因即可。
            AsyncCompletionProbe.RecordBrokerFailure(exception);
            SqlAssistDiagnostics.WriteAlways($"查詢非同步 IntelliSense 支援狀態失敗：{exception.Message}");
        }
    }
}
