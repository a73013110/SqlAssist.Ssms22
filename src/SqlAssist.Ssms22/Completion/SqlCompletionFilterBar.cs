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
/// 的按鈕狀態換回分類。外觀、滑鼠、Alt＋快捷鍵與佈景主題都由平台負責，過濾不是——
/// <see cref="SqlAsyncCompletionItemManager"/> 把 <see cref="Selected"/> 交給
/// <see cref="SuggestionList.Update"/>，再用 <see cref="States"/> 把結果畫回按鈕。
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

        InitialStates = States(SuggestionCategorySet.Empty, present);
    }

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

    /// <summary>使用者目前按著的分類，讀自平台交回來的按鈕狀態（含剛按的那一下）。</summary>
    public SuggestionCategorySet Selected(ImmutableArray<CompletionFilterWithState> states)
    {
        var selected = SuggestionCategorySet.Empty;

        foreach (var state in states)
        {
            if (state.IsSelected && CategoryOf(state.Filter) is { } category)
            {
                selected = selected.With(category);
            }
        }

        return selected;
    }

    /// <summary>要畫回去的按鈕：沒有命中的變灰，套用中的按下。</summary>
    /// <remarks>
    /// 數量與順序每一輪都一樣：平台在使用者按鈕那條路上要求交回同樣多顆，否則整批作廢。
    /// </remarks>
    public ImmutableArray<CompletionFilterWithState> States(
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
