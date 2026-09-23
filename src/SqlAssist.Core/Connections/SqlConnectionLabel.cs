using System;

namespace SqlAssist.Core.Connections;

/// <summary>一條連線的伺服器與資料庫名稱；不含連線字串、密碼或 Token。</summary>
[Serializable]
public sealed record SqlConnectionLabel(string Server, string Database);
