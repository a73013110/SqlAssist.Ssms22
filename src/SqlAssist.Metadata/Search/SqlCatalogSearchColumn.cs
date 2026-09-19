using System;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 索引裡的一個資料行：名字加上它屬於誰。
/// </summary>
/// <remarks>
/// 直接指回 <see cref="SqlObjectInfo"/> 而不是只記 <c>object_id</c>：回報一筆命中要的是
/// 結構描述、名稱、種類與資料庫，照編號回頭查等於每一筆命中都做一次字典查詢，
/// 而那正是掃描迴圈裡最熱的地方。同一個物件的所有資料行共用同一個參考，不會多佔記憶體。
/// </remarks>
public sealed class SqlCatalogSearchColumn
{
    public SqlCatalogSearchColumn(SqlObjectInfo owner, string name)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));

        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("資料行名稱不可為空。", nameof(name));
        }

        Name = name;
    }

    public SqlObjectInfo Owner { get; }

    public string Name { get; }

    public override string ToString() => Owner.QualifiedName + "." + Name;
}
