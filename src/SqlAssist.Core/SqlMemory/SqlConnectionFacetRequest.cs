using System;

namespace SqlAssist.Core.SqlMemory;

public enum SqlConnectionFacetSort { Recent, Oldest, Alphabetical, ReverseAlphabetical }

/// <summary>只讀連線名稱，不讀 SQL；與清單分頁互不影響。</summary>
[Serializable]
public sealed class SqlConnectionFacetRequest
{
    /// <summary>每頁顯示的名稱數；儲存層多回一筆，呼叫端據此判斷還有沒有下一頁。</summary>
    public const int PageSize = 100;

    public SqlConnectionFacetRequest(bool favorites, SqlFavoriteScope scope, bool databases,
        string? server = null, SqlConnectionFacetSort sort = SqlConnectionFacetSort.Recent, int offset = 0)
    {
        if (!Enum.IsDefined(typeof(SqlFavoriteScope), scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (!Enum.IsDefined(typeof(SqlConnectionFacetSort), sort)) throw new ArgumentOutOfRangeException(nameof(sort));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        IsFavorites = favorites; Scope = scope; Databases = databases; Server = server; Sort = sort; Offset = offset;
    }

    public bool IsFavorites { get; }
    public SqlFavoriteScope Scope { get; }
    public bool Databases { get; }
    public string? Server { get; }
    public SqlConnectionFacetSort Sort { get; }
    public int Offset { get; }
}
