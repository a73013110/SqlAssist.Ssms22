using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Wildcards;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 每個編輯器共用一份中繼資料服務與 ALTER 展開器。
/// </summary>
/// <remarks>
/// 建議來源、排名器與提交管理員是三個獨立的 MEF 匯出，但它們必須看到同一份
/// 中繼資料快取，否則同一個編輯器會對同一個資料庫查詢三次。
/// </remarks>
internal static class SqlCompletionServices
{
    private static readonly object SyncRoot = new();

    public static SqlMetadataService GetMetadataService(ITextView textView, IServiceProvider serviceProvider)
    {
        lock (SyncRoot)
        {
            return textView.Properties.GetOrCreateSingletonProperty(
                typeof(SqlMetadataService),
                () =>
                {
                    var service = new SqlMetadataService(serviceProvider);
                    textView.Closed += (_, _) => service.Dispose();
                    return service;
                });
        }
    }

    public static SqlWildcardExpander GetWildcardExpander(ITextView textView, IServiceProvider serviceProvider)
    {
        lock (SyncRoot)
        {
            return textView.Properties.GetOrCreateSingletonProperty(
                typeof(SqlWildcardExpander),
                () => new SqlWildcardExpander(textView, GetMetadataService(textView, serviceProvider)));
        }
    }

    public static SqlModuleExpander GetModuleExpander(ITextView textView, IServiceProvider serviceProvider)
    {
        lock (SyncRoot)
        {
            return textView.Properties.GetOrCreateSingletonProperty(
                typeof(SqlModuleExpander),
                () => new SqlModuleExpander(textView, GetMetadataService(textView, serviceProvider)));
        }
    }
}

[Export(typeof(IAsyncCompletionSourceProvider))]
[Name("SqlAssist SSMS 22 Async Completion Source")]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class SqlAsyncCompletionSourceProvider : IAsyncCompletionSourceProvider
{
    [Import]
    internal SVsServiceProvider ServiceProvider { get; set; } = null!;

    /// <remarks>
    /// 平台在按鍵路徑上呼叫這個方法，丟出例外會讓整條建議管線在該編輯器裡失效
    /// 並冒出錯誤對話框；建立失敗就安靜地不參與。
    /// </remarks>
    public IAsyncCompletionSource? GetOrCreate(ITextView textView)
    {
        return SqlAssistPlatformGuard.Create<IAsyncCompletionSource>(
            "建立建議來源",
            () => textView.Properties.GetOrCreateSingletonProperty(
                typeof(SqlAsyncCompletionSource),
                () => new SqlAsyncCompletionSource(
                    SqlCompletionServices.GetMetadataService(textView, ServiceProvider),
                    ServiceProvider)));
    }
}

/// <summary>
/// 排名器。
/// </summary>
/// <remarks>
/// 沒有這一個匯出，平台會用自己的比對器，詞首感知排名就會失效——
/// 輸入 <c>libr</c> 時 <c>Lib_Reader</c> 又會排到含子字串的名稱後面。
/// </remarks>
[Export(typeof(IAsyncCompletionItemManagerProvider))]
[Name("SqlAssist SSMS 22 Async Completion Item Manager")]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class SqlAsyncCompletionItemManagerProvider : IAsyncCompletionItemManagerProvider
{
    private readonly SqlAsyncCompletionItemManager _itemManager = new();

    public IAsyncCompletionItemManager GetOrCreate(ITextView textView) => _itemManager;
}

[Export(typeof(IAsyncCompletionCommitManagerProvider))]
[Name("SqlAssist SSMS 22 Async Completion Commit Manager")]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class SqlAsyncCompletionCommitManagerProvider : IAsyncCompletionCommitManagerProvider
{
    [Import]
    internal SVsServiceProvider ServiceProvider { get; set; } = null!;

    /// <summary>提交之後要自己把清單重開一次，那要經過 broker。</summary>
    [Import]
    internal IAsyncCompletionBroker Broker { get; set; } = null!;

    public IAsyncCompletionCommitManager? GetOrCreate(ITextView textView)
    {
        return SqlAssistPlatformGuard.Create<IAsyncCompletionCommitManager>(
            "建立提交管理員",
            () => textView.Properties.GetOrCreateSingletonProperty(
                typeof(SqlAsyncCompletionCommitManager),
                () => new SqlAsyncCompletionCommitManager(
                    SqlCompletionServices.GetModuleExpander(textView, ServiceProvider),
                    Broker)));
    }
}
