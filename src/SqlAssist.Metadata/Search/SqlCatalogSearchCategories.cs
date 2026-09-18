using System;
using System.Collections.Generic;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// <see cref="SqlObjectKind"/> 與搜尋分類之間的對應；分層的接縫就在這一支。
/// </summary>
/// <remarks>
/// Core 看不到 <see cref="SqlObjectKind"/>（相依方向是 Metadata → Core），所以分類清單
/// 不可能寫在那一層；而種類自己也不該認得搜尋——它同時服務建議清單、F12 與結構預覽。
/// 兩邊都不動，中間放這一份對應表。
///
/// Id 是<b>穩定字串</b>而不是列舉的數字：它會被寫進使用者偏好（記住上次勾了哪幾個
/// 分類），而列舉的數字會隨著中間插入一個新種類整批位移，症狀是使用者下次打開時
/// 勾的是另一種物件，而且沒有任何一處看得出來。
///
/// 顯示字走 <see cref="SqlObjectKinds.ToDisplayName"/>：同一個種類在滑鼠停留提示、
/// 結構預覽與這裡叫不同的名字，是使用者分不出兩者是不是同一件事的那種差異。
/// </remarks>
public static class SqlCatalogSearchCategories
{
    /// <summary>資料行的分類 Id；它不是一種 <see cref="SqlObjectKind"/>。</summary>
    /// <remarks>
    /// 資料行沒有自己的 <c>sys.objects</c> 列，卻是搜尋裡最常被問的東西之一
    /// （「<c>PUBL_CODE</c> 到底在哪幾張表」）。併進它所屬物件的分類的話，
    /// 過濾「只看資料表」會把資料行命中一起帶進來，而那一列講的是另一件事。
    /// </remarks>
    public const string ColumnCategoryId = "catalog.column";

    /// <summary>資料行分類的顯示字。</summary>
    private const string ColumnDisplayName = "Column";

    /// <summary>會出現在搜尋結果裡的種類，依顯示順序。</summary>
    /// <remarks>
    /// 指令碼自己宣告的三種（暫存資料表、資料表變數、CTE）不在裡面：它們一列都不在
    /// <c>sys.objects</c> 上，這個 provider 根本掃不到。<see cref="SqlObjectKind.Unknown"/>
    /// 同理——那是「認不得的型別代碼」，而不是一種可以列給人勾選的東西。
    /// </remarks>
    private static readonly SqlObjectKind[] IndexedKinds =
    {
        SqlObjectKind.Table,
        SqlObjectKind.View,
        SqlObjectKind.Procedure,
        SqlObjectKind.ScalarFunction,
        SqlObjectKind.InlineTableFunction,
        SqlObjectKind.TableValuedFunction,
        SqlObjectKind.Synonym,
        SqlObjectKind.Trigger,
        SqlObjectKind.Sequence,
        SqlObjectKind.TableType
    };

    /// <summary>
    /// 這個種類的分類 Id；不進索引的種類回傳 null。
    /// </summary>
    /// <remarks>
    /// 回 null 而不是給一個「其他」分類：使用者勾不到的分類等於一組永遠過濾不掉的
    /// 結果，而那些列點下去也沒有東西可以導航。呼叫端拿到 null 就整筆跳過。
    /// </remarks>
    public static string? IdFor(SqlObjectKind kind)
    {
        return kind switch
        {
            SqlObjectKind.Table => "catalog.table",
            SqlObjectKind.View => "catalog.view",
            SqlObjectKind.Procedure => "catalog.procedure",
            SqlObjectKind.ScalarFunction => "catalog.scalar-function",
            SqlObjectKind.InlineTableFunction => "catalog.inline-table-function",
            SqlObjectKind.TableValuedFunction => "catalog.table-valued-function",
            SqlObjectKind.Synonym => "catalog.synonym",
            SqlObjectKind.Trigger => "catalog.trigger",
            SqlObjectKind.Sequence => "catalog.sequence",
            SqlObjectKind.TableType => "catalog.table-type",
            _ => null
        };
    }

    /// <summary>宣告給 UI 的分類清單：每一種進索引的物件各一個，最後加上資料行。</summary>
    public static IReadOnlyList<SearchCategory> Create(string providerId)
    {
        if (providerId is null)
        {
            throw new ArgumentNullException(nameof(providerId));
        }

        var categories = new List<SearchCategory>(IndexedKinds.Length + 1);

        foreach (var kind in IndexedKinds)
        {
            // IdFor 對這幾種一定答得出來；答不出來表示上面那份清單與對應表分岔了，
            // 而那要在建構 provider 的當下就炸掉，不是讓某一種物件安靜地沒有分類。
            var id = IdFor(kind) ??
                throw new InvalidOperationException($"{kind} 沒有對應的搜尋分類 Id。");

            categories.Add(new SearchCategory(providerId, id, kind.ToDisplayName()));
        }

        categories.Add(new SearchCategory(providerId, ColumnCategoryId, ColumnDisplayName));
        return categories.ToArray();
    }
}
