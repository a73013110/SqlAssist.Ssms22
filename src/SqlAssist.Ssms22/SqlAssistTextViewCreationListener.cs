using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22;

[Export(typeof(IWpfTextViewCreationListener))]
[Name("SqlAssist SSMS 22 View Setup")]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class SqlAssistTextViewCreationListener : IWpfTextViewCreationListener
{
    [Import(typeof(SVsServiceProvider))]
    internal IServiceProvider ServiceProvider { get; set; } = null!;

    /// <summary>
    /// 新版非同步 IntelliSense 的 broker，結構預覽要靠它得知清單開了沒。
    /// </summary>
    /// <remarks>
    /// 允許缺席：SSMS 若沒有匯出這個實作，整個 MEF part 不可以因此組合失敗，
    /// 否則連建議清單都會跟著失效。
    /// </remarks>
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

            // 套件可能還沒載入（自動載入與第一個查詢視窗的先後順序不保證），
            // 所以設定與視窗狀態在這裡也接一次；兩者都是重複呼叫無害。
            SqlAssistSettingsStore.Initialize(ServiceProvider);
            PreviewWindowState.Initialize(ServiceProvider);

            // 結構預覽要跟著建議清單開關，也要在清單開起來的空檔先把視窗建好。
            if (PreviewServices is { } previewServices)
            {
                SqlPreviewServices.Register(previewServices);
                SqlPreviewSessionHook.Attach(textView, AsyncCompletionBroker, ServiceProvider);
            }

            // 趁編輯器剛開、SSMS 還不忙的時候先解析連線，否則第一次按鍵要付這筆成本。
            SqlCompletionServices.GetMetadataService(textView, ServiceProvider).BeginWarmup();
            SqlAssistDiagnostics.WriteAlways("SQL 編輯器已建立，SqlAssist 已載入", textView);
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"建立 SQL 編輯器時初始化 SqlAssist 失敗：{exception}");
        }
    }
}
