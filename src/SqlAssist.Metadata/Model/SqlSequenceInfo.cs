using System;

namespace SqlAssist.Metadata.Model;

/// <summary>
/// 序列在 <c>sys.sequences</c> 裡的那一列。
/// </summary>
/// <remarks>
/// 四個界限值在資料庫裡是 <c>sql_variant</c>，型別隨序列自己的型別而變
/// （<c>tinyint</c> 到 <c>decimal(38,0)</c> 都有可能）。查詢那一端已經
/// <c>CONVERT</c> 成字串，這裡就原樣收著——把它讀成 <see cref="long"/> 的話，
/// 一個 <c>decimal(38,0)</c> 的序列會在轉型那一步就溢位，而這份資料只有一個
/// 用途：原封不動寫回 <c>CREATE SEQUENCE</c>。
/// </remarks>
public sealed class SqlSequenceInfo
{
    public SqlSequenceInfo(
        string dataType,
        string startValue,
        string increment,
        string minimumValue,
        string maximumValue,
        bool isCycling,
        bool isCached,
        int? cacheSize)
    {
        if (string.IsNullOrEmpty(dataType))
        {
            throw new ArgumentException("序列的型別不可為空。", nameof(dataType));
        }

        DataType = dataType;
        StartValue = startValue ?? string.Empty;
        Increment = increment ?? string.Empty;
        MinimumValue = minimumValue ?? string.Empty;
        MaximumValue = maximumValue ?? string.Empty;
        IsCycling = isCycling;
        IsCached = isCached;
        CacheSize = cacheSize;
    }

    /// <summary>已經格式化過的型別，例如 <c>int</c>、<c>decimal(18,0)</c>。</summary>
    public string DataType { get; }

    /// <summary>目前的起始值；<c>sys.sequences.start_value</c> 記的是建立時的那一個。</summary>
    public string StartValue { get; }

    public string Increment { get; }

    public string MinimumValue { get; }

    public string MaximumValue { get; }

    public bool IsCycling { get; }

    public bool IsCached { get; }

    /// <summary>快取大小；由引擎自行決定時為 null，那時寫成不帶數字的 <c>CACHE</c>。</summary>
    public int? CacheSize { get; }
}
