using System;
using System.Collections.Generic;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>
/// 說明的值怎麼比、怎麼收：資料字典的編輯、併發檢查與同名比較共用這一條。
/// </summary>
/// <remarks>
/// 沒有說明一律是空字串，不分 NULL 與空白：使用者在格子裡清掉一段字與「從來沒寫過」看起來一樣，
/// 兩種各算一種的症狀是清空之後那一格仍然算「有變更」，而指令碼裡多一句 drop 一個不存在的屬性。
/// 前後空白不算內容：從 Excel 貼過來的值常帶著尾端空白或換行，留著的話同一句說明在兩張表上比成不一樣。
/// </remarks>
public static class SqlDescriptionValue
{
    /// <summary>
    /// 一筆說明最多幾個字。
    /// </summary>
    /// <remarks>
    /// <c>sp_addextendedproperty</c> 的值上限是 7,500 位元組，<c>nvarchar</c> 一個字兩個位元組。
    /// 寫進去才失敗的話整批交易一起退回，而使用者看到的是一句伺服器的錯誤，看不出是哪一格太長。
    /// </remarks>
    public const int MaxLength = 3750;

    public static string Normalize(string? value) => value is null ? string.Empty : value.Trim();

    public static bool AreEqual(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    public static bool IsTooLong(string? value) => Normalize(value).Length > MaxLength;
}

/// <summary>
/// 一筆說明掛在哪裡：哪一張資料表或檢視，以及（若是資料行說明）哪一個資料行。
/// </summary>
/// <remarks>
/// 以名稱為鍵而不是 <c>object_id</c>：寫回的指令碼本來就以名稱指定目標
/// （<c>sp_addextendedproperty</c> 的 level 引數），而同一份指令碼打開在查詢視窗裡、改天再執行，
/// 認的也是名稱。名稱照伺服器原樣以序數比較——它們來自同一份查詢，不需要猜定序。
/// </remarks>
public sealed class SqlDescriptionTarget : IEquatable<SqlDescriptionTarget>
{
    /// <summary><c>sys.extended_properties.name</c>：SSMS 與各家工具認的說明就是這一個。</summary>
    public const string PropertyName = "MS_Description";

    public SqlDescriptionTarget(
        SqlDictionaryObjectKind objectKind,
        string schemaName,
        string objectName,
        string? columnName = null)
    {
        if (string.IsNullOrEmpty(schemaName)) throw new ArgumentNullException(nameof(schemaName));
        if (string.IsNullOrEmpty(objectName)) throw new ArgumentNullException(nameof(objectName));

        ObjectKind = objectKind;
        SchemaName = schemaName;
        ObjectName = objectName;
        ColumnName = string.IsNullOrEmpty(columnName) ? null : columnName;
    }

    public SqlDictionaryObjectKind ObjectKind { get; }

    public string SchemaName { get; }

    public string ObjectName { get; }

    /// <summary>資料行說明時是資料行名稱；物件本身的說明時為 null。</summary>
    public string? ColumnName { get; }

    public bool IsColumn => ColumnName is not null;

    /// <summary><c>sp_addextendedproperty</c> 的 level1 型別。</summary>
    public string ParentType =>
        ObjectKind == SqlDictionaryObjectKind.View ? SqlExtendedPropertyScript.ViewParent : SqlExtendedPropertyScript.TableParent;

    /// <summary>寫成擴充屬性；值由呼叫端給（刪除時不用）。</summary>
    public SqlExtendedProperty ToProperty(string value) =>
        new(IsColumn ? SqlExtendedPropertyLevel.Column : SqlExtendedPropertyLevel.Table, PropertyName, value, ColumnName);

    /// <summary>排序：結構描述、物件、物件本身在它的資料行之前，資料行照名稱。</summary>
    /// <remarks>指令碼照這個順序寫，同一批變更每次產生的文字都一樣，檢視指令碼時才比得出差別。</remarks>
    public static IComparer<SqlDescriptionTarget> Order { get; } = new TargetOrder();

    public bool Equals(SqlDescriptionTarget? other) =>
        other is not null &&
        ObjectKind == other.ObjectKind &&
        string.Equals(SchemaName, other.SchemaName, StringComparison.Ordinal) &&
        string.Equals(ObjectName, other.ObjectName, StringComparison.Ordinal) &&
        string.Equals(ColumnName, other.ColumnName, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as SqlDescriptionTarget);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = (int)ObjectKind;
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(SchemaName);
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(ObjectName);
            return hash * 31 + (ColumnName is null ? 0 : StringComparer.Ordinal.GetHashCode(ColumnName));
        }
    }

    public override string ToString() =>
        ColumnName is null ? SchemaName + "." + ObjectName : SchemaName + "." + ObjectName + "." + ColumnName;

    private sealed class TargetOrder : IComparer<SqlDescriptionTarget>
    {
        public int Compare(SqlDescriptionTarget? x, SqlDescriptionTarget? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var result = string.CompareOrdinal(x.SchemaName, y.SchemaName);
            if (result != 0) return result;
            result = string.CompareOrdinal(x.ObjectName, y.ObjectName);
            if (result != 0) return result;
            result = x.ObjectKind.CompareTo(y.ObjectKind);
            if (result != 0) return result;
            if (x.ColumnName is null) return y.ColumnName is null ? 0 : -1;
            return y.ColumnName is null ? 1 : string.CompareOrdinal(x.ColumnName, y.ColumnName);
        }
    }
}
