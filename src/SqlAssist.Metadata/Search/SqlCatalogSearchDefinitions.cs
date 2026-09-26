using System;
using System.Collections.Generic;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 一個資料庫的定義本文，以 <c>object_id</c> 為鍵；索引的第二段。
/// </summary>
/// <remarks>
/// <b>與第一段分開的整個理由就在這個型別存不存在。</b>這一份是整份索引裡唯一沒有上界的
/// 東西——單一個模組動輒數 MB。這一輪不搜定義本文時，索引上的
/// <see cref="SqlCatalogSearchIndex.Definitions"/> 是 null，這一份根本沒有被建立過；
/// 併進物件那一段的話，「不搜本文」只會省下比對那幾毫秒。
///
/// <b>這一份只有「全部」或「沒有」兩種。</b>超過位元組上限時整份不留，索引改由伺服器端比對
/// （<see cref="SqlCatalogSearchIndex.TextOnServer"/>）。留半份的那一版要另外記「哪幾個沒留」，
/// 而漏記的那一個在畫面上與「本文裡沒有這個字」一模一樣。
///
/// 鍵用 <c>object_id</c> 而不是限定名稱：它<b>只在自己那個資料庫裡唯一</b>，而一份
/// <see cref="SqlCatalogSearchDefinitions"/> 一定屬於某一個資料庫的索引。
///
/// 不可變：同一份索引會被好幾條搜尋執行緒同時讀。
/// </remarks>
public sealed class SqlCatalogSearchDefinitions
{
    /// <summary>一份都沒有（這個資料庫真的沒有任何定義本文）。</summary>
    public static readonly SqlCatalogSearchDefinitions Empty =
        new(new Dictionary<int, string>(), new HashSet<int>(), 0);

    private readonly Dictionary<int, string> _byObjectId;
    private readonly HashSet<int> _unreadable;

    internal SqlCatalogSearchDefinitions(Dictionary<int, string> byObjectId, HashSet<int> unreadable, long bytes)
    {
        _byObjectId = byObjectId;
        _unreadable = unreadable;
        Bytes = bytes;
    }

    /// <summary>留下來的位元組數；索引快取的位元組預算靠它算。</summary>
    public long Bytes { get; }

    public int Count => _byObjectId.Count;

    /// <summary>這個物件的定義本文；沒有時為 null。</summary>
    public string? For(int objectId) => _byObjectId.TryGetValue(objectId, out var definition) ? definition : null;

    /// <summary>
    /// 這個物件有模組，但本文讀不到（加密，或這個登入沒有 VIEW DEFINITION）。
    /// </summary>
    /// <remarks>
    /// 記下來而不是略過：略過的症狀是搜 <c>sp_executesql</c> 時一個加密的預存程序安靜地不出現，
    /// 而畫面上說「沒有相符項目」。完整度那一層據此說出「有幾個物件的本文讀不到」。
    /// </remarks>
    public bool IsUnreadable(int objectId) => _unreadable.Contains(objectId);

    /// <summary>
    /// 一位一位收進來，順便算位元組；超過上限時整份作廢。
    /// </summary>
    /// <remarks>
    /// 收集期間可變、收完之後不可變：讓 <see cref="SqlCatalogSearchDefinitions"/> 自己長大的話，
    /// 讀它的那幾條搜尋執行緒會看到一份正在改的字典。
    /// </remarks>
    internal sealed class Builder
    {
        private readonly Dictionary<int, string> _byObjectId = new();
        private readonly HashSet<int> _unreadable = new();
        private readonly long _maxBytes;
        private long _bytes;

        internal Builder(long maxBytes)
        {
            if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            _maxBytes = maxBytes;
        }

        /// <summary>
        /// 超過上限了；呼叫端停止讀取，這個資料庫改由伺服器端比對。
        /// </summary>
        /// <remarks>
        /// 上限算的是 UTF-16 的位元組，所以用 <c>Length * 2</c>：照字元數算的話上限會與實際佔用
        /// 差一倍，而「64 MiB」這種數字寫在設定上是要對得起來的。
        /// </remarks>
        internal bool IsOverflowed { get; private set; }

        internal int Count => _byObjectId.Count + _unreadable.Count;

        internal void Add(int objectId, string definition)
        {
            if (definition is null) throw new ArgumentNullException(nameof(definition));
            if (IsOverflowed) return;

            var cost = (long)definition.Length * sizeof(char);

            if (_bytes + cost > _maxBytes)
            {
                IsOverflowed = true;
                _byObjectId.Clear();
                return;
            }

            // 同一個編號重複出現是資料有問題，不是這裡要救的事；後到的覆蓋前一個，
            // 位元組兩份都算，寧可高估。
            _bytes += cost;
            _byObjectId[objectId] = definition;
            _unreadable.Remove(objectId);
        }

        /// <summary>這個物件有模組而本文是 NULL。</summary>
        internal void AddUnreadable(int objectId)
        {
            if (!_byObjectId.ContainsKey(objectId)) _unreadable.Add(objectId);
        }

        /// <summary>沿用上一份索引裡沒有變更過的那一份，連同「讀不到」一起。</summary>
        internal void Reuse(SqlCatalogSearchDefinitions previous, int objectId)
        {
            if (previous.For(objectId) is { } definition) Add(objectId, definition);
            else if (previous.IsUnreadable(objectId)) AddUnreadable(objectId);
        }

        /// <returns>超過上限時為 null。</returns>
        internal SqlCatalogSearchDefinitions? Build() =>
            IsOverflowed ? null : new SqlCatalogSearchDefinitions(_byObjectId, _unreadable, _bytes);
    }
}
