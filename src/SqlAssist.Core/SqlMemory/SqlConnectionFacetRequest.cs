using System;

namespace SqlAssist.Core.SqlMemory;

public enum SqlConnectionFacetSort { Recent, Oldest, Alphabetical, ReverseAlphabetical }

/// <summary>只讀連線名稱，不讀 SQL；與清單分頁互不影響。</summary>
[Serializable]
public sealed class SqlConnectionFacetRequest
{
    /// <summary>每頁顯示的名稱數；儲存層多回一筆，呼叫端據此判斷還有沒有下一頁。</summary>
    public const int PageSize = 100;

    /// <param name="favorites">讀收藏標註；否則讀 History 的連線。</param>
    /// <param name="server">只列這台伺服器底下的資料庫；null 表示所有伺服器。</param>
    public SqlConnectionFacetRequest(bool favorites, bool databases, string? server = null,
        SqlConnectionFacetSort sort = SqlConnectionFacetSort.Recent, int offset = 0)
    {
        if (!Enum.IsDefined(typeof(SqlConnectionFacetSort), sort)) throw new ArgumentOutOfRangeException(nameof(sort));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        IsFavorites = favorites; Databases = databases; Server = server; Sort = sort; Offset = offset;
    }

    public bool IsFavorites { get; }
    public bool Databases { get; }
    public string? Server { get; }
    public SqlConnectionFacetSort Sort { get; }
    public int Offset { get; }
}
