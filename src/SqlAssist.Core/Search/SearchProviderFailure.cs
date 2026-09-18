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
    public SearchProviderFailure(string providerId, Exception exception)
    {
        ProviderId = SearchArgument.Identifier(providerId, nameof(providerId));
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public string ProviderId { get; }

    public Exception Exception { get; }

    public string Message => Exception.Message;

    public override string ToString() => $"{ProviderId}: {Message}";
}
