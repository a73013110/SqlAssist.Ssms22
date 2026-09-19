using System;
using System.Collections.Generic;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 一個資料庫的定義本文，以 <c>object_id</c> 為鍵；索引的第二段。
/// </summary>
/// <remarks>
/// <b>與第一段分開的整個理由就在這個型別存不存在。</b>這一份是整份索引裡唯一沒有上界的
/// 東西——單一個模組動輒數 MB。這一輪不搜定義本文時，索引上的
/// <see cref="SqlCatalogSearchIndex.Definitions"/> 是 null，這一份根本沒有被建立過，
/// 也沒有被傳回來過；併進物件那一段的話，「不搜本文」只會省下比對那幾毫秒。
///
/// 鍵用 <c>object_id</c> 而不是限定名稱：它<b>只在自己那個資料庫裡唯一</b>，而一份
/// <see cref="SqlCatalogSearchDefinitions"/> 一定屬於某一個資料庫的索引，
/// 所以在這個範圍裡它是唯一的，而且不必為每一筆再組一次字串。
///
/// 不可變：同一份索引會被好幾條搜尋執行緒同時讀。
/// </remarks>
public sealed class SqlCatalogSearchDefinitions
{
    /// <summary>一份都沒有、而且是完整的（這個資料庫真的沒有任何定義本文）。</summary>
    public static readonly SqlCatalogSearchDefinitions Empty =
        new(new Dictionary<int, string>(), 0, isComplete: true);

    private readonly Dictionary<int, string> _byObjectId;

    /// <param name="isComplete">
    /// 位元組上限沒有用盡；false 表示後面的物件只剩名稱，這一輪的本文命中不完整。
    /// </param>
    internal SqlCatalogSearchDefinitions(Dictionary<int, string> byObjectId, long bytes, bool isComplete)
    {
        _byObjectId = byObjectId;
        Bytes = bytes;
        IsComplete = isComplete;
    }

    /// <summary>
    /// 定義本文有沒有全部收進來。
    /// </summary>
    /// <remarks>
    /// 不在單一個物件上分「沒有本文」與「沒收進來」——分在那裡的話每一個物件都要多一個
    /// 旗標，而使用者要知道的是「這一輪的結果完不完整」。安靜地少一半結果是最糟的：
    /// 使用者會以為那個字串在這個資料庫裡不存在。
    /// </remarks>
    public bool IsComplete { get; }

    /// <summary>留下來的位元組數；索引快取的位元組預算靠它算。</summary>
    public long Bytes { get; }

    public int Count => _byObjectId.Count;

    /// <summary>這個物件的定義本文；沒有時為 null。</summary>
    public string? For(int objectId) => _byObjectId.TryGetValue(objectId, out var definition) ? definition : null;

    /// <summary>
    /// 一位一位收進來，順便算位元組並在上限用盡時停手。
    /// </summary>
    /// <remarks>
    /// 收集期間可變、收完之後不可變：讓 <see cref="SqlCatalogSearchDefinitions"/> 自己長大的話，
    /// 讀它的那幾條搜尋執行緒會看到一份正在改的字典。
    /// </remarks>
    internal sealed class Builder
    {
        private readonly Dictionary<int, string> _byObjectId = new();
        private readonly long _maxBytes;
        private long _bytes;
        private bool _isComplete = true;

        internal Builder(long maxBytes)
        {
            if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            _maxBytes = maxBytes;
        }

        /// <summary>上限已經用盡；再加也只會被丟掉。</summary>
        internal bool IsExhausted => !_isComplete;

        /// <summary>
        /// 加一份定義本文；上限用盡時標記不完整並丟掉它。
        /// </summary>
        /// <remarks>
        /// 上限算的是<b>留下來</b>的位元組。已經串流回來的那一列還是得讀過去（不讀就沒有
        /// 下一列），差別在於不把字串留下來，讓它當場可以回收。UTF-16 一個字元兩個位元組，
        /// 所以用 <c>Length * 2</c> 算——照字元數算的話上限會與實際佔用差一倍，
        /// 而「64 MiB」這種數字寫在設定上是要對得起來的。
        /// </remarks>
        internal void Add(int objectId, string definition)
        {
            if (definition is null) throw new ArgumentNullException(nameof(definition));

            var cost = (long)definition.Length * sizeof(char);

            if (_bytes + cost > _maxBytes)
            {
                _isComplete = false;
                return;
            }

            // 同一個編號重複出現是資料有問題，不是這裡要救的事；後到的覆蓋前一個，
            // 但位元組只加一次那一份的差額會算不準——直接兩份都算，寧可高估。
            _bytes += cost;
            _byObjectId[objectId] = definition;
        }

        /// <summary>沿用上一份索引裡沒有變更過的那幾份。</summary>
        internal void Reuse(SqlCatalogSearchDefinitions previous, int objectId)
        {
            if (previous.For(objectId) is { } definition) Add(objectId, definition);
        }

        /// <param name="inheritIncomplete">
        /// 上一份索引就已經不完整；沿用它的那幾份因此也不完整，即使這一輪沒有用盡上限。
        /// </param>
        internal SqlCatalogSearchDefinitions Build(bool inheritIncomplete = false) =>
            new(_byObjectId, _bytes, _isComplete && !inheritIncomplete);
    }
}
