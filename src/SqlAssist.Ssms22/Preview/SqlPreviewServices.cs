using System;
using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 結構預覽需要的編輯器服務。
/// </summary>
/// <remarks>
/// 預覽視窗不是 MEF 元件——它由建議清單的按鍵處理、提示視窗的連結與工具選單
/// 三條路徑建立，這些呼叫端手上只有 <see cref="ITextView"/>。
/// 因此把服務集中在一個 MEF 元件裡，由編輯器建立時的接聽器登記成靜態實例。
/// </remarks>
[Export]
internal sealed class SqlPreviewServices
{
    private static SqlPreviewServices? _current;

    [Import]
    internal ITextEditorFactoryService EditorFactory { get; set; } = null!;

    [Import]
    internal ITextBufferFactoryService BufferFactory { get; set; } = null!;

    [Import]
    internal IContentTypeRegistryService ContentTypeRegistry { get; set; } = null!;

    /// <summary>已登記的服務；MEF 尚未組合出任何 SQL 編輯器時為 null。</summary>
    public static SqlPreviewServices? Current => Volatile.Read(ref _current);

    /// <summary>由編輯器建立接聽器呼叫；重複呼叫沒有副作用。</summary>
    public static void Register(SqlPreviewServices services)
    {
        if (services is not null)
        {
            Volatile.Write(ref _current, services);
        }
    }

    /// <summary>預覽緩衝區的內容類型；找不到定義時退回純文字，只是少了著色。</summary>
    public IContentType GetPreviewContentType()
    {
        try
        {
            return ContentTypeRegistry.GetContentType(SqlPreviewDefinitions.ContentTypeName)
                ?? BufferFactory.TextContentType;
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"解析預覽內容類型失敗：{exception.Message}");
            return BufferFactory.TextContentType;
        }
    }
}
