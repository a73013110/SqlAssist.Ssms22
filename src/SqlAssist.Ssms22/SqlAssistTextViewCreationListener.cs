using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Preview;

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

    /// <summary>
    /// 結構預覽需要的編輯器服務。
    /// </summary>
    /// <remarks>
    /// 預覽由按鍵處理與提示連結建立，那些呼叫端拿不到 MEF 容器，
    /// 所以在這裡登記成靜態實例。
    ///
    /// 允許缺席：這個元件一旦組合失敗，整個接聽器都不會執行，
    /// 連建議清單都會跟著失效——為了一個預覽視窗付這個代價不值得。
    /// </remarks>
    [Import(AllowDefault = true)]
    internal SqlPreviewServices? PreviewServices { get; set; }

    /// <remarks>
    /// 這個方法由編輯器建立流程直接呼叫，丟出例外會讓整個 SQL 編輯器開不起來，
    /// 因此一律收斂：擴充功能失效總比查詢視窗打不開好。
    /// </remarks>
    public void TextViewCreated(IWpfTextView textView)
    {
        try
        {
            SqlAssistRuntimeState.MarkTextViewCreated();
            ActiveSqlEditor.Track(textView); // 工具選單的命令需要知道游標在哪個編輯器。
            RecordAsyncCompletionSupport(textView);
            var controller = new SqlCompletionController(textView, ServiceProvider, CompletionBroker);
            CompletionSessionRegistry.Register(textView, controller);

            // 本擴充的清單開起來時，把 SSMS 內建的那一份收掉；兩份同時存在會讓
            // 舊版語言服務在退格時對著已經換掉的狀態算範圍。
            NativeIntelliSenseSuppressor.Attach(textView, CompletionBroker, AsyncCompletionBroker);

            // 結構預覽要跟著建議清單開關，也要在清單開起來的空檔先把視窗建好。
            if (PreviewServices is { } previewServices)
            {
                SqlPreviewServices.Register(previewServices);
                SqlPreviewSessionHook.Attach(textView, AsyncCompletionBroker, ServiceProvider);
            }

            // 趁編輯器剛開、SSMS 還不忙的時候先解析連線，否則第一次按鍵要付這筆成本。
            SqlCompletionServices.GetMetadataService(textView, ServiceProvider).BeginWarmup();
            SqlAssistDiagnostics.WriteAlways("SQL 編輯器已建立，SqlAssist 建議控制器已載入", textView);
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"建立 SQL 編輯器時初始化 SqlAssist 失敗：{exception}");
        }
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
