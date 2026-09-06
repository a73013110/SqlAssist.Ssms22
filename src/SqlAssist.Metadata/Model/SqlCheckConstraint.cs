using System;

namespace SqlAssist.Metadata.Model;

/// <summary>資料表上的一個 <c>CHECK</c> 條件約束。</summary>
public sealed class SqlCheckConstraint
{
    public SqlCheckConstraint(
        string name,
        string definition,
        bool isDisabled = false,
        bool isNotForReplication = false,
        bool isSystemNamed = false,
        string? columnName = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("條件約束名稱不可為空。", nameof(name));
        }

        Name = name;
        Definition = definition ?? string.Empty;
        IsDisabled = isDisabled;
        IsNotForReplication = isNotForReplication;
        IsSystemNamed = isSystemNamed;
        ColumnName = columnName;
    }

    public string Name { get; }

    /// <summary>括號完整的運算式，例如 <c>([Status]&gt;=(1))</c>。</summary>
    public string Definition { get; }

    /// <summary>
    /// 條件約束目前是停用的。
    /// </summary>
    /// <remarks>
    /// 一定要寫進指令碼。省略的話重建出來的資料表會開始擋掉來源允許的資料，
    /// 而那是在資料匯入到一半才發現的那種差異。
    /// </remarks>
    public bool IsDisabled { get; }

    public bool IsNotForReplication { get; }

    /// <summary>名稱是引擎自己配的（<c>CK__Loan__Status__2A4B</c> 這種）。</summary>
    public bool IsSystemNamed { get; }

    /// <summary>寫在單一資料行上的條件約束；寫在資料表層級時為 null。</summary>
    public string? ColumnName { get; }

    public override string ToString() => $"{Name} CHECK {Definition}";
}
