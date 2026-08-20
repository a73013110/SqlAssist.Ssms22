using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

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

    public void TextViewCreated(IWpfTextView textView)
    {
        SqlAssistRuntimeState.MarkTextViewCreated();
        var controller = new SqlCompletionController(textView, ServiceProvider, CompletionBroker);
        CompletionSessionRegistry.Register(textView, controller);
        SqlAssistDiagnostics.WriteAlways("SQL 編輯器已建立，SqlAssist 建議控制器已載入", textView);
    }
}
