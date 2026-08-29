using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Ssms22;

namespace SqlAssist.Ssms22.QuickInfo;

[Export(typeof(IAsyncQuickInfoSourceProvider))]
[Name("SqlAssist SSMS 22 Object Quick Info")]
[ContentType("SQL")]
[Order(Before = "Default Quick Info Presenter")]
internal sealed class SqlQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
{
    [Import(typeof(SVsServiceProvider))]
    internal IServiceProvider ServiceProvider { get; set; } = null!;

    public IAsyncQuickInfoSource? TryCreateQuickInfoSource(ITextBuffer textBuffer)
    {
        if (textBuffer is null)
        {
            return null;
        }

        // 每個緩衝區一個來源；實際的查詢與快取由共用的中繼資料目錄負責。
        return SqlAssistPlatformGuard.Create<IAsyncQuickInfoSource>(
            "建立物件提示來源",
            () => textBuffer.Properties.GetOrCreateSingletonProperty(
                () => new SqlQuickInfoSource(textBuffer, ServiceProvider)));
    }
}
