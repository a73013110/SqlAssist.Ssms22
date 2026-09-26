using System;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 建議清單開不開的唯一規則。
/// </summary>
/// <remarks>
/// 建議來源與「結束詞元之後要不要重開」問的是同一件事。以前各寫一份：來源在
/// Ssms22 裡看觸發字元數，重開那一份是它的簡化副本，分析器的有效性裡又混著
/// 「空前綴而且目標是 Any 就無效」——三份之間任何一份改了，症狀都是某個位置
/// 打字有清單、打分隔字元卻沒有，或者反過來。
///
/// 判斷只跟文字有關，放在 Core 才能完整單元測試。
/// </remarks>
public static class SqlCompletionPolicy
{
    /// <summary>
    /// 這個上下文要不要讓建議來源參與。
    /// </summary>
    /// <param name="context">游標前方文字的分析結果。</param>
    /// <param name="triggerAfterCharacters">
    /// 沒有其他線索時，要打幾個字元才開清單。
    /// </param>
    /// <remarks>
    /// 有限定字或目標已經收斂時不必等字元數：<c>dbo.</c> 與 <c>FROM </c> 已經把
    /// 範圍講完了。其餘位置空前綴就開的話，按一下空白鍵就是整個資料庫。
    /// </remarks>
    public static bool Participates(SqlCompletionContext context, int triggerAfterCharacters)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        return OffersItems(context.Slot) &&
            (context.QualifierPath is not null ||
             context.Target != CompletionTarget.Any ||
             context.Prefix.Length >= triggerAfterCharacters);
    }

    /// <summary>這一格有沒有東西可列。</summary>
    /// <remarks>
    /// 一定是新名字的那一格沒有：清單裡沒有一項會是對的。可能是名字的那一格有，
    /// 但要軟選，見 <see cref="UsesSoftSelection"/>。
    /// </remarks>
    public static bool OffersItems(SqlCompletionSlot slot) =>
        slot is SqlCompletionSlot.Grammar or SqlCompletionSlot.MaybeName;

    /// <summary>清單開啟時預設不選中任何一項。</summary>
    /// <remarks>
    /// 可能是名字的那一格，打到一半的 <c>WHE</c> 與別名分不出來。硬選的話使用者
    /// 打完別名按 Enter，別名就被換成清單第一項；軟選時只有 Tab 提交，Enter 照常換行，
    /// 而按一下 ↓ 就轉成一般的硬選。
    /// </remarks>
    public static bool UsesSoftSelection(SqlCompletionContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        return context.Slot == SqlCompletionSlot.MaybeName;
    }
}
