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
/// 分類只回答「這是哪一種<b>東西</b>」。「對上的是它的哪裡」是另一條軸
/// （<see cref="SearchMatchTarget"/>）——<b>資料行因此不是一種分類</b>：
/// 一個資料行命中講的是「這張資料表上有一行叫這個名字」，而使用者勾「只看資料表」時要的
/// 正是它。把資料行做成分類的症狀就是那一勾會讓它整組消失。
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
    /// <summary>條件約束的分類 Id；<c>CHECK</c>、<c>DEFAULT</c>、主索引鍵／唯一鍵與外來鍵共用。</summary>
    public const string ConstraintCategoryId = "catalog.constraint";

    /// <summary>收納桶的分類 Id：序列、同義字與資料表型別共用。</summary>
    /// <remarks>
    /// 三種各自一顆 pill 的話，過濾列上會多出三顆幾乎沒有人會單獨勾的東西，而真正常用的
    /// 資料表與程序被擠到看不見。併成一桶並排在最後（<see cref="OtherSortOrder"/>）之後，
    /// 它們仍然搜得到、仍然過濾得掉，只是不再佔住前排。
    /// </remarks>
    public const string OtherCategoryId = "catalog.other";

    /// <summary>目錄物件那一群的 <see cref="SearchCategory.GroupId"/>。</summary>
    public const string ObjectGroupId = "catalog.objects";

    /// <summary>收納桶的顯示字。</summary>
    private const string OtherDisplayName = "Other";

    /// <summary>收納桶的排序；大到之後再加幾種物件也插不到它後面。</summary>
    private const int OtherSortOrder = 1000;

    /// <summary>會出現在搜尋結果裡的種類，依顯示順序。</summary>
    /// <remarks>
    /// 指令碼自己宣告的三種（暫存資料表、資料表變數、CTE）不在裡面：它們一列都不在
    /// <c>sys.objects</c> 上，這個 provider 根本掃不到。<see cref="SqlObjectKind.Unknown"/>
    /// 同理——那是「認不得的型別代碼」，而不是一種可以列給人勾選的東西。
    ///
    /// 收納桶那三種擺在最後，順序就是這份清單的順序；<see cref="Create"/> 會把它們併成一顆。
    /// </remarks>
    private static readonly SqlObjectKind[] IndexedKinds =
    {
        SqlObjectKind.Table,
        SqlObjectKind.View,
        SqlObjectKind.Procedure,
        SqlObjectKind.ScalarFunction,
        SqlObjectKind.InlineTableFunction,
        SqlObjectKind.TableValuedFunction,
        SqlObjectKind.Trigger,
        SqlObjectKind.Constraint,
        SqlObjectKind.Synonym,
        SqlObjectKind.Sequence,
        SqlObjectKind.TableType
    };

    /// <summary>
    /// 這個種類的分類 Id；不進索引的種類回傳 null。
    /// </summary>
    /// <remarks>
    /// 回 null 而不是塞進 <see cref="OtherCategoryId"/>：收納桶收的是「知道是什麼、
    /// 只是不值得單獨一顆 pill」的三種，而 null 那一族是「這條路徑不知道那是什麼」，
    /// 點下去也沒有東西可以打開。混在一起的症狀是清單上出現一列打不開的結果。
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
            SqlObjectKind.Trigger => "catalog.trigger",
            SqlObjectKind.Constraint => ConstraintCategoryId,
            SqlObjectKind.Synonym or SqlObjectKind.Sequence or SqlObjectKind.TableType => OtherCategoryId,
            _ => null
        };
    }

    /// <summary>宣告給 UI 的分類清單，依 <see cref="SearchCategory.SortOrder"/> 的順序。</summary>
    public static IReadOnlyList<SearchCategory> Create(string providerId)
    {
        if (providerId is null)
        {
            throw new ArgumentNullException(nameof(providerId));
        }

        var categories = new List<SearchCategory>(IndexedKinds.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sortOrder = 0;

        foreach (var kind in IndexedKinds)
        {
            // IdFor 對這幾種一定答得出來；答不出來表示上面那份清單與對應表分岔了，
            // 而那要在建構 provider 的當下就炸掉，不是讓某一種物件安靜地沒有分類。
            var id = IdFor(kind) ??
                throw new InvalidOperationException($"{kind} 沒有對應的搜尋分類 Id。");

            // 收納桶的三種對到同一個 Id，只宣告一顆。
            if (!seen.Add(id)) continue;

            var isOther = string.Equals(id, OtherCategoryId, StringComparison.Ordinal);

            categories.Add(new SearchCategory(
                providerId,
                id,
                isOther ? OtherDisplayName : kind.ToDisplayName(),
                isOther ? OtherSortOrder : sortOrder++,
                isOther ? OtherCategoryId : ObjectGroupId));
        }

        return categories.ToArray();
    }
}
