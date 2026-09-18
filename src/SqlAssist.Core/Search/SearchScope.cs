using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Search;

/// <summary>
/// 這一輪要搜哪幾台伺服器、哪幾個資料庫；兩份清單都空的表示不限制。
/// </summary>
/// <remarks>
/// Core 只當字串載體，不解讀也不比對：連結伺服器可以直接以位址命名
/// （<c>[192.0.2.10]</c>），資料庫名稱的大小寫規則由定序決定，而這兩件事都要連上去
/// 才答得出來。在這裡先做一次「看起來像不像名稱」的判斷，症狀是 provider 拿到的範圍
/// 與使用者選的那一個不一樣，而且錯在哪一層看不出來。
/// </remarks>
public sealed class SearchScope
{
    /// <summary>不限制範圍；沒有指定時的預設值。</summary>
    public static readonly SearchScope All = new(null, null);

    public SearchScope(IEnumerable<string>? servers, IEnumerable<string>? databases)
    {
        Servers = Copy(servers, nameof(servers));
        Databases = Copy(databases, nameof(databases));
    }

    /// <summary>伺服器名稱；空表示不限制。內容是不透明字串。</summary>
    public IReadOnlyList<string> Servers { get; }

    /// <summary>資料庫名稱；空表示不限制。內容是不透明字串。</summary>
    public IReadOnlyList<string> Databases { get; }

    public bool IsUnbounded => Servers.Count == 0 && Databases.Count == 0;

    private static IReadOnlyList<string> Copy(IEnumerable<string>? values, string parameterName)
    {
        if (values is null) return Array.Empty<string>();

        var copy = new List<string>();

        foreach (var value in values)
        {
            // null 進不了「不解讀」的豁免範圍：它會在 provider 那頭變成 NRE，
            // 而堆疊指向的是 provider 而不是組出這個範圍的人。
            if (value is null) throw new ArgumentException("範圍名稱不可為 null。", parameterName);
            copy.Add(value);
        }

        return copy.Count == 0 ? Array.Empty<string>() : copy.ToArray();
    }
}
