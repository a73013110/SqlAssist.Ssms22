using System;
using SqlAssist.Core.Keywords;

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
    /// 候選集合不封閉時，要打幾個字元才開清單。
    /// </param>
    /// <remarks>
    /// 封閉的位置空前綴就開，其餘等使用者打字：<c>SELECT </c> 之後接得了常值、括號與運算式，
    /// 按一下空白鍵就開的話是整個資料庫。
    /// </remarks>
    public static bool Participates(SqlCompletionContext context, int triggerAfterCharacters)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        return OffersItems(context.Slot) &&
            (IsClosed(context) || context.Prefix.Length >= triggerAfterCharacters);
    }

    /// <summary>
    /// 候選集合封閉：下一個詞元一定是清單上的某一項，使用者還沒打字就知道要列什麼。
    /// </summary>
    /// <remarks>
    /// 這幾種線索說的都是同一件事，各自補的話漏掉的那一種沒有徵兆，只是那個位置要多打一個字：
    /// <list type="bullet">
    /// <item>限定字（<c>dbo.</c>、<c>a.</c>）與左方括號：這一格是一個名稱。</item>
    /// <item>目標收斂（<c>FROM </c>、<c>EXEC </c>、<c>DATEADD(</c>、封閉的子句片語）。可能是名字的那一格不算：
    /// 目標說的是要哪一類既有名稱，那一格卻還寫得出清單外的新名字（<c>CREATE PROCEDURE </c> 之後的
    /// 結構描述或新程序名、<c>CREATE OR ALTER PROCEDURE </c> 之後的既有或新程序）。算的話片段的名稱欄位
    /// 一進格就開，Tab 把 <c>dbo</c> 提交進 <c>dbo.ProcedureName</c>。</item>
    /// <item>文法指定了資料行的所屬資料表（<c>UPDATE t SET </c>、<c>INSERT INTO t (</c>）：
    /// 省略掉的限定字。</item>
    /// <item>只接得了那幾個字的關鍵字位置（<c>ORDER </c>、MERGE 的 <c>THEN </c>），
    /// 見 <see cref="SqlKeywordPositionExtensions.IsClosed"/>。</item>
    /// </list>
    /// </remarks>
    public static bool IsClosed(SqlCompletionContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        return context.QualifierPath is not null ||
            context.Bracketed ||
            (context.Target != CompletionTarget.Any && context.Slot != SqlCompletionSlot.MaybeName) ||
            context.ColumnOwner is not null ||
            context.KeywordPosition.IsClosed();
    }

    /// <summary>這一格有沒有東西可列。</summary>
    /// <remarks>
    /// 一定是新名字的那一格沒有：清單裡沒有一項會是對的。可能是名字的那一格有，
    /// 但要軟選，見 <see cref="UsesSoftSelection(SqlCompletionSlot, string)"/>。
    /// </remarks>
    public static bool OffersItems(SqlCompletionSlot slot) =>
        slot is SqlCompletionSlot.Grammar or SqlCompletionSlot.MaybeName;

    /// <summary>清單開啟時預設不選中任何一項。</summary>
    /// <remarks>
    /// 開清單當下使用者打的字就是 <see cref="SqlCompletionContext.Prefix"/>（片段欄位的預設值
    /// 不算，分析已截掉）。
    /// </remarks>
    public static bool UsesSoftSelection(SqlCompletionContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        return UsesSoftSelection(context.Slot, context.Prefix);
    }

    /// <summary>使用者打了 <paramref name="typedText"/> 之後，清單預設不選中任何一項。</summary>
    /// <remarks>
    /// 兩種情形 Enter 都該是換行：
    /// <list type="bullet">
    /// <item>還沒打字：清單是自己開的（空白、逗號、左括號、片段接續），使用者按 Enter 多半是要換行。
    /// <c>SET a = 1,⏎</c> 的逗號一打清單就開，硬選的話 Enter 會插進第一個資料行。</item>
    /// <item>可能是名字的那一格：打到一半的 <c>WHE</c> 與別名分不出來，硬選的話別名按 Enter
    /// 就被換成清單第一項。</item>
    /// </list>
    /// 軟選時只有 Tab 提交，按一下 ↓ 轉成硬選。打了字之後才硬選：那時清單是照他打的字篩出來的，
    /// Enter 要的就是第一項。
    /// </remarks>
    public static bool UsesSoftSelection(SqlCompletionSlot slot, string typedText)
    {
        if (typedText is null)
        {
            throw new ArgumentNullException(nameof(typedText));
        }

        return slot == SqlCompletionSlot.MaybeName || typedText.Length == 0;
    }
}
