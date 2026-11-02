using System;
using System.Linq;

namespace SqlAssist.Core.Connections;

/// <summary>一條連線的伺服器與資料庫名稱；不含連線字串、密碼或 Token。</summary>
[Serializable]
public sealed record SqlConnectionLabel(string Server, string Database)
{
    /// <summary>給人看的「伺服器 · 資料庫」；缺的那一段不佔位。</summary>
    public string DisplayName => string.Join(" · ", new[] { Server, Database }.Where(part => part.Length > 0));
}
