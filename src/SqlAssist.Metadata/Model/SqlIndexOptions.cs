using System;
using System.Collections.Generic;

namespace SqlAssist.Metadata.Model;

/// <summary>
/// 一個索引的 <c>WITH (…)</c> 選項。
/// </summary>
/// <remarks>
/// 只有與預設值<b>不同</b>的那幾個值得寫進指令碼。全部寫出來（SSMS 的做法）
/// 會讓每一個索引後面掛七、八個 <c>= OFF</c>，而那些字沒有一個帶資訊；
/// 全部不寫則會漏掉真的調過的 <c>FILLFACTOR</c> 與 <c>DATA_COMPRESSION</c>，
/// 那是靜靜地改掉一張表的行為。
///
/// 「預設值是什麼」因此是這個型別的職責，不是排版那一層的：兩邊各記一份的話，
/// 其中一份忘了某個選項的預設值，那個選項就會在每一份指令碼裡出現。
/// </remarks>
public sealed class SqlIndexOptions
{
    /// <summary>全部都是預設值。</summary>
    public static readonly SqlIndexOptions Default = new();

    /// <summary><c>fill_factor</c> 是 0 代表沒有指定，不是 0%。</summary>
    private const byte UnspecifiedFillFactor = 0;

    private const string NoCompression = "NONE";

    public SqlIndexOptions(
        byte fillFactor = UnspecifiedFillFactor,
        bool isPadded = false,
        bool ignoreDuplicateKey = false,
        bool allowRowLocks = true,
        bool allowPageLocks = true,
        bool isDisabled = false,
        bool noRecompute = false,
        string? dataCompression = null)
    {
        FillFactor = fillFactor;
        IsPadded = isPadded;
        IgnoreDuplicateKey = ignoreDuplicateKey;
        AllowRowLocks = allowRowLocks;
        AllowPageLocks = allowPageLocks;
        IsDisabled = isDisabled;
        NoRecompute = noRecompute;
        DataCompression = dataCompression;
    }

    public byte FillFactor { get; }

    public bool IsPadded { get; }

    public bool IgnoreDuplicateKey { get; }

    public bool AllowRowLocks { get; }

    public bool AllowPageLocks { get; }

    /// <summary>
    /// 索引目前是停用的。
    /// </summary>
    /// <remarks>
    /// 停用的索引在目錄檢視上仍然看得到定義，但它沒有資料。重建時要跟著停用，
    /// 否則那張表會多出一個來源上不存在的索引——寫入會慢下來，而查詢計畫也會改變。
    /// </remarks>
    public bool IsDisabled { get; }

    /// <summary><c>STATISTICS_NORECOMPUTE</c>；來源是 <c>sys.stats.no_recompute</c>。</summary>
    public bool NoRecompute { get; }

    /// <summary><c>data_compression_desc</c>，例如 <c>PAGE</c>；<c>NONE</c> 與 null 都是沒有壓縮。</summary>
    public string? DataCompression { get; }

    /// <summary>
    /// 只列出與預設值不同的那幾個，照 <c>WITH (…)</c> 裡的寫法。
    /// </summary>
    /// <remarks>
    /// <c>IsDisabled</c> 刻意不在這裡：<c>WITH</c> 括號裡沒有這個選項，
    /// 停用要另外一個 <c>ALTER INDEX … DISABLE</c> 敘述。
    /// </remarks>
    public IReadOnlyList<string> DescribeNonDefaults()
    {
        var items = new List<string>();

        if (IsPadded)
        {
            items.Add("PAD_INDEX = ON");
        }

        if (FillFactor != UnspecifiedFillFactor)
        {
            items.Add("FILLFACTOR = " + FillFactor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (IgnoreDuplicateKey)
        {
            items.Add("IGNORE_DUP_KEY = ON");
        }

        if (NoRecompute)
        {
            items.Add("STATISTICS_NORECOMPUTE = ON");
        }

        // 這兩個的預設是 ON，所以只有被關掉時才值得寫。
        if (!AllowRowLocks)
        {
            items.Add("ALLOW_ROW_LOCKS = OFF");
        }

        if (!AllowPageLocks)
        {
            items.Add("ALLOW_PAGE_LOCKS = OFF");
        }

        if (!string.IsNullOrEmpty(DataCompression) &&
            !string.Equals(DataCompression, NoCompression, StringComparison.OrdinalIgnoreCase))
        {
            items.Add("DATA_COMPRESSION = " + DataCompression);
        }

        return items;
    }
}
