using System;

namespace SqlAssist.Core.Search;

/// <summary>
/// 某一個 provider 這一輪失敗了。
/// </summary>
/// <remarks>
/// 失敗被收成資料而不是往外丟：搜尋同時問好幾個來源，其中一個連不上資料庫
/// 不該讓另外四個已經算好的結果消失。呼叫端拿得到這一份就可以在清單旁邊
/// 說明少了哪一個來源，而不是整個面板空白。
/// </remarks>
public sealed class SearchProviderFailure
{
    /// <param name="displayName">
    /// 這個來源在畫面上叫什麼；空的就退回 <paramref name="providerId"/>。
    /// </param>
    public SearchProviderFailure(string providerId, string? displayName, Exception exception)
    {
        ProviderId = SearchArgument.Identifier(providerId, nameof(providerId));
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? ProviderId : displayName!;
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public string ProviderId { get; }

    /// <summary>
    /// 說給人聽的來源名稱。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="ProviderId"/> 分開：Id 是跨版本不得更名的識別字，寫進畫面的話使用者
    /// 讀到的是「『catalog』這一輪失敗」——那個字不在介面上任何地方出現過，他無從對應到
    /// 自己勾的哪一個範圍。診斷仍然記 Id。
    /// </remarks>
    public string DisplayName { get; }

    public Exception Exception { get; }

    public string Message => Exception.Message;

    public override string ToString() => $"{ProviderId}: {Message}";
}
