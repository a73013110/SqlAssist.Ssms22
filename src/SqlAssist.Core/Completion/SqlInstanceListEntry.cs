using System;

namespace SqlAssist.Core.Completion;

/// <summary>伺服器回答的名單裡的一項。</summary>
public sealed class SqlInstanceListEntry
{
    public SqlInstanceListEntry(string name, string? detail = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail;
    }

    /// <summary>寫進指令碼的那個名稱。</summary>
    public string Name { get; }

    /// <summary>
    /// 清單列尾的說明：語言的別名（<c>Deutsch</c> 的 <c>German</c>）、時區目前的 UTC 位移；
    /// 沒有時為 <c>null</c>，列尾改寫種類。
    /// </summary>
    /// <remarks>
    /// 語言的名稱是當地寫法，使用者記得的常是英文別名；列在旁邊才找得到。
    /// </remarks>
    public string? Detail { get; }
}
