using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text.Adornments;
using SqlAssist.Core.Completion;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 建議清單上方那排分類篩選鈕。
/// </summary>
/// <remarks>
/// 外觀、鍵盤（Alt+存取鍵）、滑鼠與佈景主題都由平台負責，這裡只要在每一項掛上
/// 它所屬的分類。但實際的過濾動作平台不會代勞——清單是
/// <see cref="SqlAsyncCompletionItemManager"/> 產生的，選了哪幾顆得自己去
/// <c>AsyncCompletionSessionDataSnapshot.SelectedFilters</c> 讀。
///
/// 每個分類只能有一顆 <see cref="CompletionFilter"/> 實體：平台以物件本身比對
/// 選取狀態，而 <see cref="CompletionFilter"/> 沒有覆寫 <c>Equals</c>，
/// 每次新建一顆的話，按下去的那顆永遠對不上項目掛的那顆。
/// </remarks>
internal static class SqlCompletionFilters
{
    private static readonly ImmutableArray<CompletionFilter> Columns =
        One("欄位", "c", SqlIcons.GetImageElement(SuggestionKind.Column));

    private static readonly ImmutableArray<CompletionFilter> Tables =
        One("資料表", "t", SqlIcons.GetImageElement(SuggestionKind.Table));

    private static readonly ImmutableArray<CompletionFilter> Views =
        One("檢視", "v", SqlIcons.GetImageElement(SuggestionKind.View));

    private static readonly ImmutableArray<CompletionFilter> Procedures =
        One("預存程序", "p", SqlIcons.GetImageElement(SuggestionKind.Procedure));

    private static readonly ImmutableArray<CompletionFilter> ScalarFunctions =
        One("純量函式", "f", SqlIcons.GetImageElement(SuggestionKind.Function));

    private static readonly ImmutableArray<CompletionFilter> TableFunctions =
        One("資料表值函式（含內嵌與多敘述）", "r", SqlIcons.GetImageElement(SuggestionKind.TableFunction));

    private static readonly ImmutableArray<CompletionFilter> BuiltInFunctions =
        One("內建函式", "b", SqlIcons.GetImageElement(SuggestionKind.BuiltInFunction));

    private static readonly ImmutableArray<CompletionFilter> Keywords =
        One("關鍵字", "k", SqlIcons.GetImageElement(SuggestionKind.Keyword));

    private static readonly ImmutableArray<CompletionFilter> Snippets =
        One("程式碼片段", "s", SqlIcons.GetImageElement(SuggestionKind.Snippet));

    private static readonly ImmutableArray<CompletionFilter> Others =
        One("其他", "o", SqlIcons.Ellipsis);

    /// <summary>
    /// 篩選鈕由左到右的順序。
    /// </summary>
    /// <remarks>
    /// 必須自己排：平台推出來的順序來自項目清單，而清單是
    /// 「關鍵字與程式碼片段 → 敘述範圍欄位 → 資料庫物件」的串接，
    /// 照那個順序畫出來，第一顆會是關鍵字，資料表要排到第四、五顆去。
    ///
    /// 這裡的順序是「使用者在寫 SQL 時想到的順序」：先是敘述裡的欄位，
    /// 再來是資料表，然後才是建立在資料表之上的檢視、預存程序與函式；
    /// 打字時隨手可得的關鍵字與片段放後面，沒有分類的東西收在最後。
    /// </remarks>
    private static readonly CompletionFilter[] Order =
    {
        Columns[0],
        Tables[0],
        Views[0],
        Procedures[0],
        ScalarFunctions[0],
        TableFunctions[0],
        BuiltInFunctions[0],
        Keywords[0],
        Snippets[0],
        Others[0]
    };

    /// <summary>
    /// 建議項所屬的分類。
    /// </summary>
    /// <remarks>
    /// 函式按使用方式分成內建、純量與資料表值，避免大量內建函式淹沒資料庫函式。
    /// 內嵌與多敘述同屬資料表值函式，呼叫位置相同，不再按實作方式拆按鈕；
    /// 真正物件種類仍由項目說明與預覽呈現。篩選只縮小既有候選，不擴張 SQL 語境。
    ///
    /// 結構描述、資料庫、型別與小老鼠開頭的那幾類沒有自己的篩選鈕——它們分別只
    /// 出現在 <c>USE</c>、型別位置、<c>@@</c> 與 <c>@</c> 之後，當下清單裡幾乎只有
    /// 一類，給它一顆按了也不會有任何變化——但仍然歸到「其他」，
    /// 而不是留成沒有分類。每一項都有分類，按下任何一顆篩選鈕之後，
    /// 剩下的就一定是那一類，不會有「沒被篩掉但也不屬於任何一顆」的漏網項目。
    /// </remarks>
    public static ImmutableArray<CompletionFilter> For(SuggestionKind kind)
    {
        return kind switch
        {
            SuggestionKind.Column => Columns,

            // CTE 與暫存資料表歸在「資料表」：它們在使用者眼中就是資料表，
            // 分成兩顆按鈕只是讓他多按一次才找得到。
            SuggestionKind.Table or SuggestionKind.ScriptDataSource => Tables,
            SuggestionKind.View => Views,
            SuggestionKind.Procedure => Procedures,
            SuggestionKind.Function => ScalarFunctions,
            SuggestionKind.TableFunction => TableFunctions,
            SuggestionKind.BuiltInFunction => BuiltInFunctions,
            SuggestionKind.Keyword => Keywords,
            SuggestionKind.Snippet => Snippets,
            SuggestionKind.Schema
                or SuggestionKind.Database
                or SuggestionKind.GlobalVariable
                or SuggestionKind.Variable
                or SuggestionKind.DataType
                or SuggestionKind.Parameter
                or SuggestionKind.Trigger
                or SuggestionKind.Sequence
                or SuggestionKind.UserDefinedType
                or SuggestionKind.DatePart
                or SuggestionKind.TableHint
                or SuggestionKind.QueryHint
                or SuggestionKind.LinkedServer => Others,
            _ => Others
        };
    }

    /// <summary>
    /// 把篩選列排成 <see cref="Order"/> 的順序。
    /// </summary>
    /// <remarks>
    /// 交給平台的 <c>FilteredCompletionModel</c> 就是畫出來的那一排，
    /// 所以順序在這裡決定。選取與可用狀態原封不動帶著走，只換位置。
    /// </remarks>
    public static ImmutableArray<CompletionFilterWithState> Sort(
        ImmutableArray<CompletionFilterWithState> states)
    {
        if (states.Length < 2)
        {
            return states;
        }

        var builder = ImmutableArray.CreateBuilder<CompletionFilterWithState>(states.Length);

        foreach (var filter in Order)
        {
            foreach (var state in states)
            {
                if (ReferenceEquals(state.Filter, filter))
                {
                    builder.Add(state);
                    break;
                }
            }
        }

        // 認不得的篩選器照原順序補回去：位置不對，總比整顆從篩選列上消失好。
        foreach (var state in states)
        {
            if (Array.IndexOf(Order, state.Filter) < 0)
            {
                builder.Add(state);
            }
        }

        return builder.Count == states.Length ? builder.ToImmutable() : states;
    }

    /// <summary>
    /// 這份清單裡是否有兩種以上的分類。
    /// </summary>
    /// <remarks>
    /// 只有一種分類時整批都不掛篩選器，篩選列就不會出現——一整排只有一顆、
    /// 而且按了畫面不會變的按鈕，只是白白佔掉清單上方一條。
    /// </remarks>
    public static bool HasMultipleCategories(IReadOnlyList<SqlSuggestion> suggestions)
    {
        CompletionFilter? first = null;

        foreach (var suggestion in suggestions)
        {
            var filter = For(suggestion.Kind)[0];

            if (first is null)
            {
                first = filter;
                continue;
            }

            if (!ReferenceEquals(first, filter))
            {
                return true;
            }
        }

        return false;
    }

    // 僅供靜態初始化呼叫；For() 必須重用同一顆篩選器，才能保留平台的選取狀態。
    private static ImmutableArray<CompletionFilter> One(string displayText, string accessKey, ImageElement image)
    {
        return ImmutableArray.Create(new CompletionFilter(displayText, accessKey, image));
    }
}
