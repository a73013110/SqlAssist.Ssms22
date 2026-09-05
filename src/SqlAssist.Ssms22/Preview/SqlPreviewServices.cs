using System;
using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Ssms22;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 結構預覽需要的編輯器服務。
/// </summary>
/// <remarks>
/// 預覽視窗不是 MEF 元件——它由建議清單的按鍵處理、提示視窗的連結與工具選單
/// 三條路徑建立，這些呼叫端手上只有 <c>ITextView</c>。
/// 因此把服務集中在一個 MEF 元件裡，由編輯器建立時的接聽器登記成靜態實例。
/// </remarks>
[Export]
internal sealed class SqlPreviewServices
{
    private static SqlPreviewServices? _current;

    [Import]
    internal IClassificationFormatMapService FormatMapService { get; set; } = null!;

    [Import]
    internal IClassificationTypeRegistryService ClassificationRegistry { get; set; } = null!;

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

    /// <summary>編輯器目前佈景主題的文字外觀；取不到時回傳 null，由呼叫端退回預設值。</summary>
    public IClassificationFormatMap? TryGetTextFormatMap(ITextView view)
    {
        return SqlAssistPlatformGuard.Probe<IClassificationFormatMap?>(
            "解析編輯器文字外觀",
            () => FormatMapService.GetClassificationFormatMap(view),
            fallback: null);
    }
}
