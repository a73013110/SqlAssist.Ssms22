using System;

namespace SqlAssist.Metadata.Model;

/// <summary>
/// 一個東西存在哪裡：檔案群組，或分割配置加上分割資料行。
/// </summary>
/// <remarks>
/// 兩者在 <c>sys.data_spaces</c> 裡是同一張表、同一個名稱欄位，差別只在
/// <c>type</c>——而寫出來的 T-SQL 完全不同：檔案群組是 <c>ON [PRIMARY]</c>，
/// 分割配置是 <c>ON [ps_Loan]([LoanTime])</c>。把分割配置當成檔案群組寫，
/// 得到的是一段語法錯誤的指令碼。
/// </remarks>
public sealed class SqlDataSpace
{
    /// <summary><c>sys.data_spaces.type</c> 的分割配置代碼。</summary>
    private const string PartitionSchemeType = "PS";

    public SqlDataSpace(string name, string? type = null, string? partitionColumnName = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("資料空間名稱不可為空。", nameof(name));
        }

        Name = name;
        Type = type ?? string.Empty;
        PartitionColumnName = partitionColumnName;
    }

    public string Name { get; }

    /// <summary><c>FG</c> 檔案群組、<c>PS</c> 分割配置、<c>FD</c> FILESTREAM。</summary>
    public string Type { get; }

    /// <summary>分割資料行；不是分割配置時為 null。</summary>
    public string? PartitionColumnName { get; }

    public bool IsPartitionScheme =>
        Type.Trim().Equals(PartitionSchemeType, StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        IsPartitionScheme && PartitionColumnName is not null
            ? $"{Name}({PartitionColumnName})"
            : Name;
}

/// <summary>資料表本身的儲存位置與建立當時的 SET 選項。</summary>
public sealed class SqlTableStorage
{
    /// <summary>查不到時的空值：什麼都不寫，而不是猜一個 <c>[PRIMARY]</c>。</summary>
    public static readonly SqlTableStorage None = new();

    public SqlTableStorage(
        SqlDataSpace? dataSpace = null,
        string? lobFilegroupName = null,
        bool usesAnsiNulls = true,
        bool usesQuotedIdentifier = true)
    {
        DataSpace = dataSpace;
        LobFilegroupName = lobFilegroupName;
        UsesAnsiNulls = usesAnsiNulls;
        UsesQuotedIdentifier = usesQuotedIdentifier;
    }

    /// <summary>資料表的檔案群組或分割配置；查不到時為 null。</summary>
    public SqlDataSpace? DataSpace { get; }

    /// <summary>
    /// <c>TEXTIMAGE_ON</c> 的檔案群組；沒有 LOB 資料行時為 null。
    /// </summary>
    /// <remarks>
    /// 判斷「要不要寫 <c>TEXTIMAGE_ON</c>」一律問這一個欄位，不要自己掃資料行的
    /// 型別：<c>xml</c>、CLR 型別與 <c>varchar(max)</c> 都算，漏一種就是一份與
    /// 來源不同的資料表。
    /// </remarks>
    public string? LobFilegroupName { get; }

    /// <summary>建立當時的 <c>SET ANSI_NULLS</c>。</summary>
    public bool UsesAnsiNulls { get; }

    /// <summary>建立當時的 <c>SET QUOTED_IDENTIFIER</c>。</summary>
    public bool UsesQuotedIdentifier { get; }
}
