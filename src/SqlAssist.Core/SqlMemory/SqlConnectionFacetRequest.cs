using System;
using System.Collections.Generic;

namespace SqlAssist.Core.SqlMemory;

public enum SqlConnectionFacetSort { Recent, Oldest, Alphabetical, ReverseAlphabetical }

/// <summary>只讀連線名稱，不讀 SQL；與清單分頁互不影響。</summary>
[Serializable]
public sealed class SqlConnectionFacetRequest
{
    /// <summary>每頁顯示的名稱數；儲存層多回一筆，呼叫端據此判斷還有沒有下一頁。</summary>
    public const int PageSize = 100;

    /// <param name="favorites">讀收藏標註；否則讀 History 的連線。</param>
    /// <param name="servers">只列這幾台伺服器底下的資料庫；空名單表示所有伺服器。</param>
    public SqlConnectionFacetRequest(bool favorites, bool databases, IEnumerable<string>? servers = null,
        SqlConnectionFacetSort sort = SqlConnectionFacetSort.Recent, int offset = 0)
    {
        if (!Enum.IsDefined(typeof(SqlConnectionFacetSort), sort)) throw new ArgumentOutOfRangeException(nameof(sort));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        IsFavorites = favorites; Databases = databases; Sort = sort; Offset = offset;
        Servers = SqlConnectionNames.Normalize(servers);
    }

    public bool IsFavorites { get; }
    public bool Databases { get; }

    /// <summary>資料庫名單的伺服器範圍；空名單表示不限，讀伺服器名單時一律忽略。</summary>
    public IReadOnlyList<string> Servers { get; }

    public SqlConnectionFacetSort Sort { get; }
    public int Offset { get; }
}
