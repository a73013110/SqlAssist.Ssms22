using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 建議清單篩選列的規則：列哪幾顆、哪幾顆可按、按下之後清單剩什麼。
/// </summary>
/// <remarks>
/// 篩選列依 <see cref="SuggestionCategory"/> 的順序排這份清單真的有的分類。
/// 快捷鍵照畫面位置編，與鍵盤上數字列的順序相同：由左至右 Alt+1～9，第十顆是 Alt+0。
/// 沒有按下任何一顆就是全部。
///
/// 規則只有一條：<b>沒有命中的分類不能處於按下狀態</b>。由它推出使用者看得到的全部行為——
/// 打字之後沒有命中的那幾顆變灰並跳起來；按下的全部跳起來就等於回到全部；灰色的
/// 按下去不會有反應；清單因此不會出現「按鈕還按著、底下卻什麼都沒有」的死路。
/// 跳起來的按鈕不會在命中回來時自己再按下去：那要記住一份畫面上看不到的狀態，
/// 而使用者只會看到按鈕自己動了。
/// </remarks>
public static class SuggestionCategoryFilter
{
    /// <summary>
    /// 分類鈕最多幾顆。
    /// </summary>
    /// <remarks>
    /// 數字列只有十個鍵，平台又要求每顆按鈕都要有快捷鍵。一般位置的清單最多十類——序列只在
    /// 自己的位置出現——所以這個上限實際上碰不到；真的超過時，多出來的分類沒有自己的按鈕，
    /// 只在什麼都沒按時出現。
    /// </remarks>
    public const int MaximumButtons = 10;

    /// <summary>
    /// 篩選列上的分類鈕，依畫面順序。
    /// </summary>
    /// <remarks>
    /// 只有一類時不列：一整排只有一顆、按了畫面不會變的按鈕，只是白白佔掉清單上方一條。
    /// </remarks>
    public static IReadOnlyList<SuggestionCategory> Buttons(SuggestionCategorySet present)
    {
        return present.Count < 2
            ? Array.Empty<SuggestionCategory>()
            : present.InOrder().Take(MaximumButtons).ToArray();
    }

    /// <summary>
    /// 這一輪實際套用的分類；空集合代表全部。
    /// </summary>
    /// <param name="selected">使用者目前按著的分類。</param>
    /// <param name="matched">這一輪有命中的分類。</param>
    public static SuggestionCategorySet Apply(SuggestionCategorySet selected, SuggestionCategorySet matched)
    {
        return selected.Intersect(matched);
    }

    /// <summary>這個分類的項目在套用 <paramref name="applied"/> 之後是否留在清單上。</summary>
    public static bool Includes(SuggestionCategorySet applied, SuggestionCategory category)
    {
        return applied.IsEmpty || applied.Contains(category);
    }
}
