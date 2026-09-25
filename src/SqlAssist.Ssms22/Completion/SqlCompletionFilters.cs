using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text.Adornments;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;
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
    private sealed class FilterSet
    {
        public ImmutableArray<CompletionFilter> Columns { get; } =
            One(SqlKindText.Columns, "c", SqlIcons.GetImageElement(SuggestionKind.Column));

        public ImmutableArray<CompletionFilter> Tables { get; } =
            One(SqlKindText.Tables, "t", SqlIcons.GetImageElement(SuggestionKind.Table));

        public ImmutableArray<CompletionFilter> Views { get; } =
            One(SqlKindText.Views, "v", SqlIcons.GetImageElement(SuggestionKind.View));

        public ImmutableArray<CompletionFilter> Procedures { get; } =
            One(SqlKindText.Procedures, "p", SqlIcons.GetImageElement(SuggestionKind.Procedure));

        public ImmutableArray<CompletionFilter> ScalarFunctions { get; } =
            One(SqlKindText.ScalarFunctions, "f", SqlIcons.GetImageElement(SuggestionKind.Function));

        public ImmutableArray<CompletionFilter> TableFunctions { get; } =
            One(CompletionText.FilterTableFunctions, "r", SqlIcons.GetImageElement(SuggestionKind.TableFunction));

        public ImmutableArray<CompletionFilter> BuiltInFunctions { get; } =
            One(SqlKindText.BuiltInFunctions, "b", SqlIcons.GetImageElement(SuggestionKind.BuiltInFunction));

        public ImmutableArray<CompletionFilter> Keywords { get; } =
            One(SqlKindText.Keywords, "k", SqlIcons.GetImageElement(SuggestionKind.Keyword));

        public ImmutableArray<CompletionFilter> Snippets { get; } =
            One(SqlKindText.Snippets, "s", SqlIcons.GetImageElement(SuggestionKind.Snippet));

        public ImmutableArray<CompletionFilter> Others { get; } =
            One(CommonText.Other, "o", SqlIcons.Ellipsis);

        public CompletionFilter[] Order { get; }

        public FilterSet()
        {
            Order = new[]
            {
                Columns[0], Tables[0], Views[0], Procedures[0], ScalarFunctions[0],
                TableFunctions[0], BuiltInFunctions[0], Keywords[0], Snippets[0], Others[0]
            };
        }
    }

    /// <summary>
    /// 每種語言一組篩選鈕。
    /// </summary>
    /// <remarks>
    /// 平台以實體比對選取狀態，同一種語言必須一直是同一組實體；換語言後新開的清單拿新的一組，
    /// 已經開著的那份清單帶著舊的一組直到關掉，<see cref="Sort"/> 照它自己那一組排。
    /// </remarks>
    private static readonly SqlLanguageCache<FilterSet> Sets = new(_ => new FilterSet());

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
    /// 定序同理（<c>COLLATE</c> 之後那份清單裡只有定序），
    /// 而不是留成沒有分類。每一項都有分類，按下任何一顆篩選鈕之後，
    /// 剩下的就一定是那一類，不會有「沒被篩掉但也不屬於任何一顆」的漏網項目。
    /// </remarks>
    public static ImmutableArray<CompletionFilter> For(SuggestionKind kind)
    {
        var set = Sets.Current;
        return kind switch
        {
            SuggestionKind.Column => set.Columns,

            // CTE、暫存資料表與資料表變數歸在「資料表」：那顆按鈕問的是「這個
            // 位置我要一張表」，而它們在那個位置就是表。獨立一顆的代價不只是多
            // 按一次——按下「資料表」之後，他上一行才寫下的 #Loan 反而消失了，
            // 而那正是他最可能要選的那一個。要單獨找它們的人打前綴更快：
            // 那個名稱是他自己剛取的。
            SuggestionKind.Table or SuggestionKind.ScriptDataSource => set.Tables,
            SuggestionKind.View => set.Views,
            SuggestionKind.Procedure => set.Procedures,
            SuggestionKind.Function => set.ScalarFunctions,
            SuggestionKind.TableFunction => set.TableFunctions,
            SuggestionKind.BuiltInFunction => set.BuiltInFunctions,
            SuggestionKind.Keyword => set.Keywords,
            SuggestionKind.Snippet => set.Snippets,
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
                or SuggestionKind.LinkedServer
                or SuggestionKind.Collation
                or SuggestionKind.CollationInUse => set.Others,
            _ => set.Others
        };
    }

    /// <summary>
    /// 把篩選列排成 <see cref="FilterSet.Order"/> 的順序。
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

        var order = OrderOf(states[0].Filter);
        var builder = ImmutableArray.CreateBuilder<CompletionFilterWithState>(states.Length);

        foreach (var filter in order)
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
            if (Array.IndexOf(order, state.Filter) < 0)
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

    /// <summary>這份篩選列是哪一種語言的那一組；清單開著時換了語言，仍照它自己那一組排。</summary>
    private static CompletionFilter[] OrderOf(CompletionFilter sample)
    {
        foreach (var language in SqlLanguage.All)
        {
            var order = Sets.For(language).Order;
            if (Array.IndexOf(order, sample) >= 0) return order;
        }

        return Sets.Current.Order;
    }

    // 僅供 FilterSet 建立時呼叫；For() 必須重用同一顆篩選器，才能保留平台的選取狀態。
    private static ImmutableArray<CompletionFilter> One(string displayText, string accessKey, ImageElement image)
    {
        return ImmutableArray.Create(new CompletionFilter(displayText, accessKey, image));
    }
}
