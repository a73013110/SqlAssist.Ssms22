using System;

namespace SqlAssist.Ssms22.UI;

/// <summary>狀態表面現在在說哪一件事。</summary>
/// <remarks>
/// <see cref="Unreadable"/> 與 <see cref="Denied"/> 走同一個出口，只有抬頭那一句不同：
/// 兩者的下一步完全不一樣（重試一次 vs. 去要權限），但畫面上都是「這裡沒有東西可看」，
/// 各做一塊版面只會讓窄工具窗再少一塊內容高度。
/// </remarks>
internal enum SqlSurfaceKind
{
    /// <summary>沒有東西要說；內容自己就是答案。</summary>
    None,

    /// <summary>這一輪還在跑，而且還沒有任何一列可看。</summary>
    Loading,

    /// <summary>讀到了，但結果是空的；抬頭由呼叫端決定，因為「空」的原因各功能不同。</summary>
    Empty,

    /// <summary>這一輪讀不到：失敗、逾時或連不上。</summary>
    Unreadable,

    /// <summary>權限不足：讀得到伺服器，但這個登入看不到這一份。</summary>
    Denied,
}

/// <summary>
/// 載入、空、錯誤與無權限四種狀態的唯一描述；SQL Memory 與 SQL Search 共用。
/// </summary>
/// <remarks>
/// 文案分成抬頭與說明兩段，不串成一句：錯誤與無權限的抬頭是固定的兩句話，說明才是
/// 呼叫端帶進來的原因。串成一句的話，每一個呼叫端都得自己記得寫上「這一輪讀不到」，
/// 而漏掉的那一個會把一句裸露的例外訊息丟在畫面正中央。
/// </remarks>
internal readonly struct SqlSurfaceState : IEquatable<SqlSurfaceState>
{
    /// <summary>讀不到的固定抬頭；重試或換條件可能就有了。</summary>
    public const string UnreadableTitle = "這一輪讀不到";

    /// <summary>無權限的固定抬頭；重試幾次都不會變，下一步是去要權限。</summary>
    public const string DeniedTitle = "權限不足";

    private readonly string? _title;
    private readonly string? _detail;

    private SqlSurfaceState(SqlSurfaceKind kind, string? title, string? detail)
    {
        Kind = kind;
        _title = title;
        _detail = detail;
    }

    /// <summary>預設值就是「沒有東西要說」；沒有設定過的表面不會蓋住內容。</summary>
    public static SqlSurfaceState None => default;

    public static SqlSurfaceState Loading => new(SqlSurfaceKind.Loading, null, null);

    /// <param name="title">例如「沒有相符項目」；空字串等於沒有東西要說。</param>
    /// <param name="detail">下一步的提示，例如放寬期間或清除搜尋。</param>
    public static SqlSurfaceState Empty(string title, string detail = "") =>
        string.IsNullOrEmpty(title) ? None : new SqlSurfaceState(SqlSurfaceKind.Empty, title, detail);

    public static SqlSurfaceState Unreadable(string detail) =>
        new(SqlSurfaceKind.Unreadable, UnreadableTitle, detail);

    public static SqlSurfaceState Denied(string detail) =>
        new(SqlSurfaceKind.Denied, DeniedTitle, detail);

    public SqlSurfaceKind Kind { get; }

    /// <summary>置中的那一行；<see cref="SqlSurfaceKind.Loading"/> 與 <see cref="SqlSurfaceKind.None"/> 為空。</summary>
    public string Title => _title ?? "";

    /// <summary>抬頭下方的淡色說明；沒有時為空字串。</summary>
    public string Detail => _detail ?? "";

    /// <summary>讀不到與無權限共用同一個出口；呈現只分抬頭，不分版面。</summary>
    public bool IsUnavailable => Kind is SqlSurfaceKind.Unreadable or SqlSurfaceKind.Denied;

    public bool Equals(SqlSurfaceState other) =>
        Kind == other.Kind &&
        string.Equals(Title, other.Title, StringComparison.Ordinal) &&
        string.Equals(Detail, other.Detail, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SqlSurfaceState other && Equals(other);

    public override int GetHashCode() =>
        ((int)Kind * 397) ^ (Title.GetHashCode() * 31) ^ Detail.GetHashCode();

    public static bool operator ==(SqlSurfaceState left, SqlSurfaceState right) => left.Equals(right);

    public static bool operator !=(SqlSurfaceState left, SqlSurfaceState right) => !left.Equals(right);
}
