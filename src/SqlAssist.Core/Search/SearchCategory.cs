using System;

namespace SqlAssist.Core.Search;

/// <summary>
/// provider 自己宣告的一種結果分類；UI 依它產生過濾用的 pill。
/// </summary>
/// <remarks>
/// 分類只回答「這是哪一種<b>東西</b>」（資料表、檢視、條件約束、執行紀錄）。「對上的是它的
/// 哪裡」是另一條軸，見 <see cref="SearchMatchTarget"/>——兩件事混在一起的症狀是
/// 「資料行」變成一種物件種類，而使用者勾「只看資料表」時，資料表上的資料行命中整組消失。
///
/// 分類清單刻意不寫死在 Core：目錄物件的種類（<c>SqlObjectKind</c>）住在 Metadata，
/// 而相依方向是 Metadata → Core，Core 看不到它；之後的 History、Favorite、Snippet
/// 也各有自己的一組。改成 Core 的列舉之後，每加一個 provider 就要回頭改 Core，
/// 而且 Core 會被迫認識它不該認識的領域。
///
/// <see cref="Id"/> 會被寫進使用者偏好（記住上次勾了哪幾個 pill），因此跨版本不得更名；
/// 要改給人看的字改 <see cref="DisplayName"/>。兩者分開的理由就是這一條：
/// 共用同一個字串的話，翻譯或改文案會讓使用者的過濾選擇整批失效。
/// </remarks>
public sealed class SearchCategory
{
    /// <param name="sortOrder">
    /// pill 的先後；越小越前面，同值時由 provider 宣告順序決定。
    /// </param>
    /// <param name="groupId">
    /// 這顆 pill 屬於哪一群；null 表示自成一群（值等於 <paramref name="id"/>）。
    /// </param>
    public SearchCategory(
        string providerId,
        string id,
        string displayName,
        int sortOrder = 0,
        string? groupId = null)
    {
        ProviderId = SearchArgument.Identifier(providerId, nameof(providerId));
        Id = SearchArgument.Identifier(id, nameof(id));
        DisplayName = SearchArgument.Identifier(displayName, nameof(displayName));
        SortOrder = sortOrder;
        GroupId = groupId is null ? Id : SearchArgument.Identifier(groupId, nameof(groupId));
    }

    /// <summary>宣告這個分類的 provider。</summary>
    public string ProviderId { get; }

    /// <summary>跨版本穩定的識別字，與 <see cref="SearchHit.CategoryId"/> 以 ordinal 比對。</summary>
    public string Id { get; }

    /// <summary>pill 上顯示的字，可以隨文案改。</summary>
    public string DisplayName { get; }

    /// <summary>
    /// pill 的先後；越小越前面。
    /// </summary>
    /// <remarks>
    /// 明著給一個數字，而不是沿用宣告順序，理由只有一個：「其他」這種收納桶要排在最後，
    /// 而它在宣告清單裡的位置由當初加進去的時間決定。少了這一個，加一種新物件就會把
    /// 收納桶往前推，而使用者每一次更新都要重新找那顆 pill 在哪裡。
    /// </remarks>
    public int SortOrder { get; }

    /// <summary>
    /// 這顆 pill 屬於哪一群；預設自成一群。
    /// </summary>
    /// <remarks>
    /// 群是給 UI 分段用的（「資料庫物件」與「其他」），不是第二層過濾：勾的仍然是
    /// <see cref="Id"/>。與 <see cref="SortOrder"/> 分開，是因為「排在最後」與「這幾顆是一夥的」
    /// 是兩件事——只有順序的話，收納桶會混在物件種類中間，而畫面上看不出它是收納桶。
    /// </remarks>
    public string GroupId { get; }

    public override string ToString() => $"{ProviderId}:{Id}";
}
