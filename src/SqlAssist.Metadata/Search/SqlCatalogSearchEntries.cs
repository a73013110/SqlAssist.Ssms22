using System;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 索引裡的一個物件：識別欄位加上定義本文。
/// </summary>
/// <remarks>
/// 不把定義本文攤進 <see cref="SqlObjectInfo"/>：那個型別在按鍵路徑上被建立成千上萬次
/// （第一層快照），多一個欄位就是每一個物件多一個參考，而讀它的四條路徑一個都用不到。
/// </remarks>
public sealed class SqlCatalogSearchObject
{
    /// <param name="definition">
    /// 定義本文；沒有本文（資料表、資料表型別）或索引的位元組上限已經用盡時為 null。
    /// 兩者的差別由 <see cref="SqlCatalogSearchIndex.HasCompleteDefinitions"/> 回答，
    /// 不在單一物件上分——分在這裡的話每一個物件都要多一個旗標，而使用者要知道的是
    /// 「這一輪的結果完不完整」。
    /// </param>
    public SqlCatalogSearchObject(SqlObjectInfo info, string? definition)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        Definition = definition;
    }

    public SqlObjectInfo Info { get; }

    /// <summary>定義本文；沒有或沒收進來時為 null。</summary>
    public string? Definition { get; }

    public override string ToString() => Info.QualifiedName;
}

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
