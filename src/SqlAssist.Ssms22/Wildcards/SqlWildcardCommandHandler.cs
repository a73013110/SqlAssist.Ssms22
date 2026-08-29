using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Completion;

namespace SqlAssist.Ssms22.Wildcards;

/// <summary>
/// Tab 鍵：把選取清單裡的 <c>*</c> 展開成完整的欄位清單。
/// </summary>
/// <remarks>
/// Tab 在編輯器裡原本有三種意思——提交建議清單、插入定位字元、把選取的幾行縮排。
/// 這裡只在其中一個很窄的情形下接手：沒有選取範圍、建議清單沒開著，而且游標
/// 正好停在一個展得開的萬用字元後面。其餘一律回報「沒有處理」讓 Tab 照原樣走。
///
/// <b>清單開著時一定要讓開。</b>本處理常式與平台的建議清單處理常式都排在
/// <c>default</c> 之前，彼此的先後順序沒有保證；不先問過 broker 的話，
/// 使用者按 Tab 想提交清單選取項時可能會變成展開萬用字元。
///
/// 這個方法在按鍵路徑上，丟出例外就是使用者按一次鍵看到一次錯誤對話框，
/// 因此一律收斂並回報沒有處理。
/// </remarks>
[Export(typeof(ICommandHandler))]
[Name("SqlAssist SSMS 22 Wildcard Command Handler")]
[Order(Before = "default")]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class SqlWildcardCommandHandler : ICommandHandler<TabKeyCommandArgs>
{
    [Import(typeof(SVsServiceProvider))]
    internal IServiceProvider ServiceProvider { get; set; } = null!;

    /// <remarks>
    /// 允許缺席：拿不到 broker 時寧可完全不接管 Tab，也不要在清單開著時搶走它。
    /// </remarks>
    [Import(AllowDefault = true)]
    internal IAsyncCompletionBroker? Broker { get; set; }

    public string DisplayName => "SqlAssist 展開萬用字元";

    public CommandState GetCommandState(TabKeyCommandArgs args) => CommandState.Unspecified;

    public bool ExecuteCommand(TabKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return SqlAssistPlatformGuard.Run(
            "處理 Tab 按鍵",
            () => Broker is not null &&
                Broker.GetSession(args.TextView) is null &&
                SqlCompletionServices
                    .GetWildcardExpander(args.TextView, ServiceProvider)
                    .TryExpand(),
            fallback: false);
    }
}
