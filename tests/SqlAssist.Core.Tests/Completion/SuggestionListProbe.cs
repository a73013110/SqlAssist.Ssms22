using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Completion;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 照產品那一條走完一份建議清單：建立清單時的上下文過濾、開場排序、這一鍵的篩選與排名。
/// </summary>
/// <remarks>
/// 原生清單的 item manager 只把平台項目轉接給 <see cref="SuggestionList"/>；這裡用建議項本身
/// 當項目，走的是同一段程式。曾經另有一份只給測試用的排名，測試守的是產品根本不走的那一個。
/// </remarks>
internal static class SuggestionListProbe
{
    private static readonly Func<SqlSuggestion, string> DisplayTextOf = static suggestion => suggestion.DisplayText;

    private static readonly Func<SqlSuggestion, SqlSuggestion?> SuggestionOf = static suggestion => suggestion;

    /// <summary>候選在這個上下文、以它的前綴打出來之後看得到的項目。</summary>
    public static IReadOnlyList<SuggestionListEntry<SqlSuggestion>> Rank(
        IEnumerable<SqlSuggestion> candidates,
        SqlCompletionContext context,
        SuggestionCategorySet selected = default)
    {
        return Update(Sort(SuggestionContextFilter.Filter(candidates, context)), context.Prefix, selected).Items;
    }

    /// <summary><see cref="Rank"/> 的建議項本身，依顯示順序。</summary>
    public static IReadOnlyList<SqlSuggestion> Match(IEnumerable<SqlSuggestion> candidates, SqlCompletionContext context)
    {
        return Rank(candidates, context).Select(entry => entry.Item).ToArray();
    }

    /// <summary>開場排序。</summary>
    public static List<SqlSuggestion> Sort(IReadOnlyList<SqlSuggestion> items)
    {
        return SuggestionList.Sort(items, SuggestionOf);
    }

    /// <summary>已排好的清單在這一鍵之後的樣子。</summary>
    public static SuggestionListView<SqlSuggestion> Update(
        IReadOnlyList<SqlSuggestion> sortedItems,
        string typedText,
        SuggestionCategorySet selected = default)
    {
        return SuggestionList.Update(sortedItems, DisplayTextOf, SuggestionOf, typedText, selected);
    }
}
