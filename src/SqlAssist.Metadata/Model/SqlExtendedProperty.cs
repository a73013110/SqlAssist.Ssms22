using System;

namespace SqlAssist.Metadata.Model;

/// <summary>擴充屬性掛在資料表的哪一層。</summary>
/// <remarks>
/// 這一層決定 <c>sp_addextendedproperty</c> 的 level1／level2 兩組引數怎麼填，
/// 而填錯的症狀不是報錯，是屬性掛到另一個東西上。
/// </remarks>
public enum SqlExtendedPropertyLevel
{
    Table,

    Column,

    Index,

    Constraint
}

/// <summary>
/// 資料表或它底下某個東西上的一筆擴充屬性。
/// </summary>
/// <remarks>
/// <c>MS_Description</c> 是最常見的一種，但不是唯一的一種——名稱照查到的原樣帶著走，
/// 只寫 <c>MS_Description</c> 會讓其他屬性在重建的指令碼裡安靜地消失。
/// </remarks>
public sealed class SqlExtendedProperty
{
    public SqlExtendedProperty(
        SqlExtendedPropertyLevel level,
        string name,
        string value,
        string? targetName = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("擴充屬性名稱不可為空。", nameof(name));
        }

        Level = level;
        Name = name;
        Value = value ?? string.Empty;
        TargetName = targetName;
    }

    public SqlExtendedPropertyLevel Level { get; }

    /// <summary><c>sys.extended_properties.name</c>，例如 <c>MS_Description</c>。</summary>
    public string Name { get; }

    /// <summary>
    /// 屬性值。
    /// </summary>
    /// <remarks>
    /// 存成字串而不是 <c>object</c>：<c>sys.extended_properties.value</c> 是
    /// <c>sql_variant</c>，查詢在伺服器端就轉成 <c>nvarchar(max)</c>——用
    /// <c>GetValue</c> 收會拿到裝箱的原生型別，而下游要的一律是寫進指令碼的那串字。
    /// </remarks>
    public string Value { get; }

    /// <summary>屬性掛在哪一個資料行、索引或條件約束上；掛在資料表本身時為 null。</summary>
    public string? TargetName { get; }

    public override string ToString() =>
        TargetName is null ? $"{Name}={Value}" : $"{Name}({TargetName})={Value}";
}
