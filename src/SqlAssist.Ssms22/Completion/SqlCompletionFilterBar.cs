using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 一份建議清單上方的分類篩選列。
/// </summary>
/// <remarks>
/// 規則在 <see cref="SuggestionCategoryFilter"/>；這裡只把分類換成平台的按鈕，再把平台交回來
/// 的按鈕狀態換回分類。外觀、滑鼠、Alt＋快捷鍵與佈景主題都由平台負責，過濾不是——清單由
/// <see cref="SqlAsyncCompletionItemManager"/> 產生，它照 <see cref="Apply"/> 的結果篩。
///
/// 每份清單建一組按鈕，不跨清單共用：快捷鍵照這份清單的畫面位置編，換一份清單位置就不同。
/// 按鈕以<b>實體</b>交給來源的 <see cref="CompletionContext"/>，而不是掛在每一項上讓平台推：
/// 推出來的順序是項目清單的串接順序，按鈕與數字就對不上。
///
/// 按鈕不可變、不記上一輪：平台交回來的狀態就是全部依據，平台作廢某一輪結果時也不會失準。
/// </remarks>
internal sealed class SqlCompletionFilterBar
{
    private readonly (SuggestionCategory Category, CompletionFilter Filter)[] _buttons;

    private SqlCompletionFilterBar(IReadOnlyList<SuggestionCategory> categories)
    {
        _buttons = new (SuggestionCategory, CompletionFilter)[categories.Count];

        var present = SuggestionCategorySet.Empty;
        for (var index = 0; index < categories.Count; index++)
        {
            var category = categories[index];
            // 與數字列同序：1～9，第十顆是 0。
            var accessKey = ((index + 1) % 10).ToString(CultureInfo.InvariantCulture);
            _buttons[index] = (category, new CompletionFilter(TextOf(category), accessKey, SqlIcons.GetImageElement(category)));
            present = present.With(category);
        }

        Present = present;
        InitialStates = States(SuggestionCategorySet.Empty, present);
    }

    /// <summary>有按鈕的分類；還沒輸入任何字時它們全都算命中。</summary>
    public SuggestionCategorySet Present { get; }

    /// <summary>交給 <see cref="CompletionContext"/> 的初始狀態：全部可按、沒有按下。</summary>
    public ImmutableArray<CompletionFilterWithState> InitialStates { get; }

    /// <summary>候選不到兩類時回傳 null，篩選列不出現。</summary>
    public static SqlCompletionFilterBar? Create(IReadOnlyList<SqlSuggestion> suggestions)
    {
        var present = SuggestionCategorySet.Empty;
        foreach (var suggestion in suggestions)
        {
            if (SuggestionCategories.Of(suggestion.Kind) is { } category)
            {
                present = present.With(category);
            }
        }

        var categories = SuggestionCategoryFilter.Buttons(present);
        return categories.Count == 0 ? null : new SqlCompletionFilterBar(categories);
    }

    /// <summary>
    /// 讀平台交回來的按鈕狀態，算出這一輪要套用的分類與要畫回去的按鈕。
    /// </summary>
    /// <param name="states">平台目前的按鈕狀態，含使用者剛按的那一下。</param>
    /// <param name="matched">這一輪有命中的分類。</param>
    public (SuggestionCategorySet Applied, ImmutableArray<CompletionFilterWithState> States) Apply(
        ImmutableArray<CompletionFilterWithState> states,
        SuggestionCategorySet matched)
    {
        var selected = SuggestionCategorySet.Empty;

        foreach (var state in states)
        {
            if (state.IsSelected && CategoryOf(state.Filter) is { } category)
            {
                selected = selected.With(category);
            }
        }

        var applied = SuggestionCategoryFilter.Apply(selected, matched);
        return (applied, States(applied, matched));
    }

    /// <remarks>
    /// 數量與順序每一輪都一樣：平台在使用者按鈕那條路上要求交回同樣多顆，否則整批作廢。
    /// </remarks>
    private ImmutableArray<CompletionFilterWithState> States(
        SuggestionCategorySet applied,
        SuggestionCategorySet matched)
    {
        var builder = ImmutableArray.CreateBuilder<CompletionFilterWithState>(_buttons.Length);

        foreach (var (category, filter) in _buttons)
        {
            builder.Add(new CompletionFilterWithState(
                filter,
                isAvailable: matched.Contains(category),
                isSelected: applied.Contains(category)));
        }

        return builder.MoveToImmutable();
    }

    private SuggestionCategory? CategoryOf(CompletionFilter filter)
    {
        foreach (var (category, button) in _buttons)
        {
            if (ReferenceEquals(button, filter))
            {
                return category;
            }
        }

        return null;
    }

    private static string TextOf(SuggestionCategory category) => category switch
    {
        SuggestionCategory.Column => SqlKindText.Columns,
        SuggestionCategory.Table => SqlKindText.Tables,
        SuggestionCategory.View => SqlKindText.Views,
        SuggestionCategory.Procedure => SqlKindText.Procedures,
        SuggestionCategory.ScalarFunction => SqlKindText.ScalarFunctions,
        SuggestionCategory.TableFunction => CompletionText.FilterTableFunctions,
        SuggestionCategory.Sequence => SqlKindText.Sequences,
        SuggestionCategory.BuiltInFunction => SqlKindText.BuiltInFunctions,
        SuggestionCategory.Keyword => SqlKindText.Keywords,
        SuggestionCategory.Snippet => SqlKindText.Snippets,
        SuggestionCategory.SchemaOrDatabase => CompletionText.FilterSchemasAndDatabases,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };
}
