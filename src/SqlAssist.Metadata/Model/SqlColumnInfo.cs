using System;
using System.Text;
using SqlAssist.Metadata.Formatting;

namespace SqlAssist.Metadata.Model;

/// <summary>資料表或檢視的單一欄位。</summary>
public sealed class SqlColumnInfo
{
    public SqlColumnInfo(
        int ordinal,
        string name,
        string dataType,
        bool isNullable,
        bool isIdentity = false,
        bool isComputed = false,
        bool isPrimaryKey = false,
        string? defaultDefinition = null,
        string? computedDefinition = null,
        bool isGeneratedAlways = false)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("欄位名稱不可為空。", nameof(name));
        }

        Ordinal = ordinal;
        Name = name;
        DataType = dataType ?? string.Empty;
        IsNullable = isNullable;
        IsIdentity = isIdentity;
        IsComputed = isComputed;
        IsPrimaryKey = isPrimaryKey;
        DefaultDefinition = defaultDefinition;
        ComputedDefinition = computedDefinition;
        IsGeneratedAlways = isGeneratedAlways;
    }

    public int Ordinal { get; }

    public string Name { get; }

    public string DataType { get; }

    public bool IsNullable { get; }

    public bool IsIdentity { get; }

    public bool IsComputed { get; }

    public bool IsPrimaryKey { get; }

    public string? DefaultDefinition { get; }

    /// <summary>計算欄位的運算式；一般欄位為 null。</summary>
    public string? ComputedDefinition { get; }

    /// <summary>
    /// 值由引擎產生的欄位：時態資料表的 <c>PERIOD FOR SYSTEM_TIME</c> 兩欄，
    /// 以及帳本資料表的異動與序號欄。
    /// </summary>
    public bool IsGeneratedAlways { get; }

    /// <summary>
    /// 這個欄位能不能出現在 <c>INSERT</c> 的資料行清單裡。
    /// </summary>
    /// <remarks>
    /// 四種都不行，而且漏掉任何一種的症狀相同——展開出來的 <c>INSERT</c> 一執行就錯：
    /// IDENTITY（要先開 <c>IDENTITY_INSERT</c>）、計算欄位、<c>rowversion</c>
    /// （<c>timestamp</c> 是它的舊名）與 <c>GENERATED ALWAYS</c> 的欄位。
    ///
    /// 判斷放在模型上而不是放在組字串的那一層：這是欄位自己的性質，
    /// 而問這個問題的表面不會只有一個。
    /// </remarks>
    public bool CanInsert =>
        !IsIdentity &&
        !IsComputed &&
        !IsGeneratedAlways &&
        !IsRowVersion;

    /// <remarks>
    /// <c>sys.columns</c> 沒有這個旗標，型別名稱就是唯一的依據。
    /// <c>rowversion</c> 是同義字，<c>sys.types</c> 回報的一律是 <c>timestamp</c>，
    /// 但兩個都認才不會因為換一個中繼資料來源就漏掉。
    /// </remarks>
    private bool IsRowVersion =>
        string.Equals(DataType, "timestamp", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(DataType, "rowversion", StringComparison.OrdinalIgnoreCase);

    /// <summary>組出接近 CREATE TABLE 寫法的單行描述。</summary>
    public string ToScriptLine()
    {
        var builder = new StringBuilder();
        builder.Append(SqlIdentifier.Quote(Name));
        builder.Append(' ');
        builder.Append(DataType);

        if (IsIdentity)
        {
            builder.Append(" IDENTITY");
        }

        builder.Append(IsNullable ? " NULL" : " NOT NULL");

        if (!string.IsNullOrEmpty(DefaultDefinition))
        {
            builder.Append(" DEFAULT ").Append(DefaultDefinition);
        }

        if (IsPrimaryKey)
        {
            builder.Append(" -- PK");
        }

        return builder.ToString();
    }

    public override string ToString() => ToScriptLine();
}
