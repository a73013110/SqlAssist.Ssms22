using System;

namespace SqlAssist.Core.Search;

/// <summary>
/// provider 掛在一筆結果上的顯示膠囊：一句字，外加一個中性的圖示代號。
/// </summary>
/// <remarks>
/// 存在的理由是「哪一台、哪一個資料庫」這種只有 provider 說得出口的脈絡。寫死在 UI 樣板裡的話，
/// 之後的 SQL Memory provider 要掛「已收藏」「執行過 12 次」就得回頭改樣板，
/// 而樣板改一次等於每一個 provider 的列都跟著變。
///
/// <see cref="IconToken"/> 是<b>中性字串</b>（<c>"server"</c>、<c>"database"</c>），
/// 不是 SSMS 的 <c>ImageMoniker</c>：Core 是 netstandard2.0，認識那個型別等於把 VS 組件拉進
/// 這一層，而分層規則說得很明白——Core 零 VS／SSMS 相依。代號換成圖示是 Ssms22 的事，
/// 認不得的代號那一層就不畫圖示，膠囊上的字照常出現。
///
/// 不可變：同一筆結果會同時被清單、預覽與導航讀到。
/// </remarks>
public sealed class SearchBadge
{
    /// <summary>伺服器的圖示代號。</summary>
    /// <remarks>
    /// 代號寫成常數而不是讓每一個 provider 各打一次字：拼錯的那一個不會報錯，
    /// 只是安靜地少一個圖示，而那在畫面上看不出是拼錯還是這一層沒有圖示可畫。
    /// </remarks>
    public const string ServerIcon = "server";

    /// <summary>資料庫的圖示代號。</summary>
    public const string DatabaseIcon = "database";

    /// <param name="iconToken">中性代號；不想要圖示時為 null。</param>
    public SearchBadge(string text, string? iconToken = null)
    {
        Text = SearchArgument.Identifier(text, nameof(text));

        // 空字串當成「有給但給了空的」，那與 null 是兩件事：前者一定是呼叫端算出來的，
        // 而算出空字串的地方通常也算錯了別的東西。
        if (iconToken is { Length: 0 })
        {
            throw new ArgumentException("圖示代號不可為空字串；不要圖示時傳 null。", nameof(iconToken));
        }

        IconToken = iconToken;
    }

    /// <summary>膠囊上的字。</summary>
    public string Text { get; }

    /// <summary>中性圖示代號；沒有圖示時為 null。</summary>
    public string? IconToken { get; }

    public override string ToString() => IconToken is null ? Text : IconToken + ":" + Text;
}
