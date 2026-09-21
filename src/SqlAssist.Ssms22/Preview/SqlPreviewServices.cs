using System;
using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
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

    [Import]
    internal IEditorFormatMapService EditorFormatMapService { get; set; } = null!;

    /// <summary>已登記的服務；MEF 尚未組合出任何 SQL 編輯器時為 null。</summary>
    public static SqlPreviewServices? Current => Volatile.Read(ref _current);

    /// <summary>
    /// 已登記的那一份，或直接向殼層的 MEF 容器要一份。
    /// </summary>
    /// <remarks>
    /// <see cref="Current"/> 是由<b>編輯器建立接聽器</b>登記的，所以「只連了資料庫、一個查詢視窗
    /// 都沒開」時它從頭到尾是 null——而工具窗的 SQL 預覽正是在那個時候還要問得出著色分類。
    /// 少了這條路的症狀是預覽裡整份 SQL 同一個顏色，而使用者會以為是高亮壞了。
    ///
    /// 要到的是<b>同一個</b>殼層容器裡的那兩個服務，不是另一份：容器只有一個，
    /// 自己 new 一個 MEF host 才會拿到對不上編輯器設定的第二份外觀。要到之後就登記起來，
    /// 之後開的查詢視窗再登記一次也沒有副作用。
    ///
    /// 只在 UI 執行緒上要：<c>GetGlobalService</c> 有執行緒相依性。取不到就回 null，
    /// 呼叫端退回自己的前景色，不是擲出——著色讀不到還畫得出 SQL，整個預覽開不起來就不行。
    /// </remarks>
    public static SqlPreviewServices? Resolve()
    {
        if (Current is { } registered) return registered;

        var resolved = SqlAssistPlatformGuard.Probe<SqlPreviewServices?>(
            "向殼層要編輯器著色服務",
            () =>
            {
                if (Package.GetGlobalService(typeof(SComponentModel)) is not IComponentModel components) return null;

                var formatMaps = components.GetService<IClassificationFormatMapService>();
                var registry = components.GetService<IClassificationTypeRegistryService>();
                var editorFormats = components.GetService<IEditorFormatMapService>();
                if (formatMaps is null || registry is null || editorFormats is null) return null;

                return new SqlPreviewServices
                {
                    FormatMapService = formatMaps,
                    ClassificationRegistry = registry,
                    EditorFormatMapService = editorFormats
                };
            },
            fallback: null);

        if (resolved is not null) Register(resolved);
        return resolved;
    }

    /// <summary>
    /// 沒有編輯器時的文字外觀；取的是查詢視窗那一份「Text Editor」外觀類別。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="TryGetTextFormatMap(ITextView)"/> 是同一份設定的兩種入口：後者跟著某一個
    /// 檢視（它可能套了自己的外觀類別），這一份問的是類別本身。名稱不得改成別的——
    /// 打錯字不會擲出，只會安靜地回一份什麼分類都沒有的空外觀。
    /// </remarks>
    public IClassificationFormatMap? TryGetDefaultTextFormatMap()
    {
        return SqlAssistPlatformGuard.Probe<IClassificationFormatMap?>(
            "解析預設文字外觀",
            () => FormatMapService.GetClassificationFormatMap("text"),
            fallback: null);
    }

    /// <summary>由編輯器建立接聽器呼叫；重複呼叫沒有副作用。</summary>
    public static void Register(SqlPreviewServices services)
    {
        if (services is not null)
        {
            Volatile.Write(ref _current, services);
        }
    }

    /// <summary>
    /// 編輯器的格式對應；「Plain Text」那一格帶著編輯器真正的底色。
    /// </summary>
    /// <remarks>
    /// 底色不寫在分類外觀裡——它畫在檢視上，所以 <c>DefaultTextProperties.BackgroundBrush</c>
    /// 通常是空的。一個查詢視窗都沒開時借不到檢視，這一格就是唯一問得到編輯器底色的地方；
    /// 少了它，分類色只好畫在工具窗的底色上，對比一不過就整份退成同一個顏色。
    /// </remarks>
    public IEditorFormatMap? TryGetEditorFormatMap()
    {
        return SqlAssistPlatformGuard.Probe<IEditorFormatMap?>(
            "解析編輯器格式對應",
            () => EditorFormatMapService.GetEditorFormatMap("text"),
            fallback: null);
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
