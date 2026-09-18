namespace SqlAssist.Core.Search;

/// <summary>
/// provider 自己宣告的一種結果分類；UI 依它產生過濾用的 pill。
/// </summary>
/// <remarks>
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
    public SearchCategory(string providerId, string id, string displayName)
    {
        ProviderId = SearchArgument.Identifier(providerId, nameof(providerId));
        Id = SearchArgument.Identifier(id, nameof(id));
        DisplayName = SearchArgument.Identifier(displayName, nameof(displayName));
    }

    /// <summary>宣告這個分類的 provider。</summary>
    public string ProviderId { get; }

    /// <summary>跨版本穩定的識別字，與 <see cref="SearchHit.CategoryId"/> 以 ordinal 比對。</summary>
    public string Id { get; }

    /// <summary>pill 上顯示的字，可以隨文案改。</summary>
    public string DisplayName { get; }

    public override string ToString() => $"{ProviderId}:{Id}";
}
