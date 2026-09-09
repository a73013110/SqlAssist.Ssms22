using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

namespace SqlAssist.Ssms22.Signatures;

/// <summary>
/// 平台來要簽章時，把備好的那一份交出去。
/// </summary>
/// <remarks>
/// 這個介面是<b>同步</b>的，而參數要問中繼資料，所以這裡什麼都不算：內容在
/// <see cref="SqlSignatureHelp.Request"/> 那一端就備好了，這裡只負責遞出去。
/// 在這裡查一次資料庫的話，那次等待發生在 UI 執行緒上。
///
/// 沒有備好的內容時交出空清單，平台會自己把 session 收掉——別人（SSMS 自己的
/// 參數資訊）觸發的 session 也會走到這裡，那些不歸我們管。
/// </remarks>
internal sealed class SqlSignatureHelpSource : ISignatureHelpSource
{
    public void AugmentSignatureHelpSession(ISignatureHelpSession session, IList<ISignature> signatures)
    {
        SqlAssistPlatformGuard.Run(
            "提供函式簽章",
            () => SqlSignatureHelp.Peek(session.TextView)?.Augment(session, signatures));
    }

    public ISignature? GetBestMatch(ISignatureHelpSession session)
    {
        // 一個名稱只有一份簽章：T-SQL 的函式不能多載。
        return session.Signatures.Count > 0 ? session.Signatures[0] : null;
    }

    public void Dispose()
    {
    }
}

[Export(typeof(ISignatureHelpSourceProvider))]
[Name("SqlAssist SSMS 22 Signature Help Source")]
[Order(Before = "default")]
[ContentType("SQL")]
internal sealed class SqlSignatureHelpSourceProvider : ISignatureHelpSourceProvider
{
    /// <remarks>
    /// 平台在按鍵路徑上呼叫這個方法，丟出例外會讓整條簽章管線在該編輯器裡失效；
    /// 建立失敗就安靜地不參與，其餘功能照常。
    /// </remarks>
    public ISignatureHelpSource? TryCreateSignatureHelpSource(ITextBuffer textBuffer)
    {
        return SqlAssistPlatformGuard.Create<ISignatureHelpSource>(
            "建立函式簽章來源",
            () => new SqlSignatureHelpSource());
    }
}
