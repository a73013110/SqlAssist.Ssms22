using System;

namespace SqlAssist.Core.Preview;

/// <summary>
/// 預覽長開之前在膠囊停多久：內容還沒到時膠囊轉著等，到了才展開。
/// </summary>
/// <remarks>
/// 規則只有一條：<b>等待被看見了，就要讓人看完。</b>內容在 <see cref="Grace"/> 之內到齊時
/// 根本看不到等待——膠囊還在長出來，接著就展開，與快取命中時一模一樣；過了 <see cref="Grace"/>
/// 進度圈才開始浮現，浮現之後至少留 <see cref="MinimumHold"/>，否則是一閃而過的雜訊，
/// 看起來像畫面抖了一下。<see cref="Ceiling"/> 之後照樣展開，抬頭的「載入中」接手說明：
/// 對著一顆膠囊等下去，使用者連要不要關掉它都無從判斷。
///
/// 「一律先停在膠囊」的那一版在快取命中（預載之後是常態）時也等一段，等於人為變慢。
/// </remarks>
public static class PreviewReveal
{
    /// <summary>這麼快就到的內容不顯示等待。</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(150);

    /// <summary>進度圈一旦浮現至少停留這麼久。</summary>
    public static readonly TimeSpan MinimumHold = TimeSpan.FromMilliseconds(250);

    /// <summary>等到這時還沒內容就展開，交給抬頭的載入狀態。</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromMilliseconds(1500);

    /// <summary>內容在出現後 <paramref name="elapsed"/> 到齊時，還要再等多久才展開。</summary>
    public static TimeSpan HoldAfterReady(TimeSpan elapsed)
    {
        if (elapsed < Grace)
        {
            return TimeSpan.Zero;
        }

        var remaining = Grace + MinimumHold - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
